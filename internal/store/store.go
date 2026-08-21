package store

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"github.com/Star-Trails/mceindex-mcp/internal/domain"
	_ "modernc.org/sqlite"
)

type ApplyPagesResult struct {
	ChangedPages   int   `json:"changedPages"`
	UnchangedPages int   `json:"unchangedPages"`
	Generation     int64 `json:"generation"`
}

// Store encapsulates the SQLite storage engine and query operations.
type Store struct {
	path string
	db   *sql.DB
	mu   sync.RWMutex
}

// NewStore opens or creates a SQLite store at the specified path (or :memory:).
func NewStore(path string) (*Store, error) {
	var connStr string
	if path == ":memory:" {
		connStr = "file::memory:?cache=shared"
	} else {
		dir := filepath.Dir(path)
		if err := os.MkdirAll(dir, 0755); err != nil {
			return nil, domain.WrapError(domain.ErrCodeDatabaseError, "Failed to create database directory", err)
		}
		connStr = path
	}

	db, err := sql.Open("sqlite", connStr)
	if err != nil {
		return nil, domain.WrapError(domain.ErrCodeDatabaseError, "Failed to open SQLite database", err)
	}

	// SQLite pragmas for performance and concurrency
	if path != ":memory:" {
		_, _ = db.Exec("PRAGMA journal_mode=WAL;")
	}
	_, _ = db.Exec("PRAGMA foreign_keys=ON;")
	_, _ = db.Exec("PRAGMA synchronous=NORMAL;")

	if err := initializeDatabase(db); err != nil {
		db.Close()
		return nil, domain.WrapError(domain.ErrCodeDatabaseError, "Failed to initialize database schema", err)
	}

	return &Store{
		path: path,
		db:   db,
	}, nil
}

// Path returns the database file path.
func (s *Store) Path() string {
	return s.path
}

// Close closes the underlying database connection.
func (s *Store) Close() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.db != nil {
		return s.db.Close()
	}
	return nil
}

// CountPages returns the total number of indexed pages.
func (s *Store) CountPages() int {
	s.mu.RLock()
	defer s.mu.RUnlock()

	var count int
	_ = s.db.QueryRow("SELECT COUNT(*) FROM pages").Scan(&count)
	return count
}

// GetGeneration returns the current global index generation version.
func (s *Store) GetGeneration() int64 {
	val, ok := s.GetMeta("index_generation")
	if !ok || val == "" {
		return 0
	}
	gen, _ := strconv.ParseInt(val, 10, 64)
	return gen
}

// GetMeta retrieves a metadata value by key.
func (s *Store) GetMeta(key string) (string, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()

	var val string
	err := s.db.QueryRow("SELECT value FROM meta WHERE key = ?", key).Scan(&val)
	if err != nil {
		return "", false
	}
	return val, true
}

// SetMeta sets a metadata key-value pair.
func (s *Store) SetMeta(key, value string) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	_, err := s.db.Exec("INSERT OR REPLACE INTO meta (key, value) VALUES (?, ?)", key, value)
	return err
}

// ListPages returns summaries of all indexed pages.
func (s *Store) ListPages() []domain.StoredPageSummary {
	s.mu.RLock()
	defer s.mu.RUnlock()

	rows, err := s.db.Query(`
		SELECT slug, label, title, source_url, fetched_at, last_checked_at, text_count, generation
		FROM pages ORDER BY slug
	`)
	if err != nil {
		return []domain.StoredPageSummary{}
	}
	defer rows.Close()

	var summaries []domain.StoredPageSummary
	for rows.Next() {
		var (
			slug, label, title, sourceURL, fetchedAtStr, lastCheckedAtStr string
			textCount                                                     int
			generation                                                    int64
		)
		if err := rows.Scan(&slug, &label, &title, &sourceURL, &fetchedAtStr, &lastCheckedAtStr, &textCount, &generation); err == nil {
			fTime, _ := time.Parse(time.RFC3339Nano, fetchedAtStr)
			if fTime.IsZero() {
				fTime, _ = time.Parse(time.RFC3339, fetchedAtStr)
			}
			cTime, _ := time.Parse(time.RFC3339Nano, lastCheckedAtStr)
			if cTime.IsZero() {
				cTime, _ = time.Parse(time.RFC3339, lastCheckedAtStr)
			}
			summaries = append(summaries, domain.StoredPageSummary{
				Slug:          slug,
				Label:         label,
				Title:         title,
				SourceURL:     sourceURL,
				FetchedAt:     fTime,
				LastCheckedAt: cTime,
				TextCount:     textCount,
				Generation:    generation,
			})
		}
	}
	return summaries
}

// FindPage looks up a stored page by slug or Chinese label (case-insensitive).
func (s *Store) FindPage(query string) *domain.StoredPage {
	s.mu.RLock()
	defer s.mu.RUnlock()

	q := strings.TrimSpace(query)
	var (
		slug, label, title, sourceURL, fetchedAtStr, lastCheckedAtStr, snapshotJSON string
		textCount                                                                   int
		generation                                                                  int64
	)

	row := s.db.QueryRow(`
		SELECT slug, label, title, source_url, fetched_at, last_checked_at, text_count, generation, snapshot_json
		FROM pages
		WHERE slug = ? COLLATE NOCASE OR label = ? COLLATE NOCASE
		ORDER BY CASE WHEN slug = ? COLLATE NOCASE THEN 0 ELSE 1 END
		LIMIT 1
	`, q, q, q)

	err := row.Scan(&slug, &label, &title, &sourceURL, &fetchedAtStr, &lastCheckedAtStr, &textCount, &generation, &snapshotJSON)
	if err != nil {
		return nil
	}

	var snapshot domain.PageSnapshot
	if err := json.Unmarshal([]byte(snapshotJSON), &snapshot); err != nil {
		return nil
	}

	fTime, _ := time.Parse(time.RFC3339Nano, fetchedAtStr)
	if fTime.IsZero() {
		fTime, _ = time.Parse(time.RFC3339, fetchedAtStr)
	}
	cTime, _ := time.Parse(time.RFC3339Nano, lastCheckedAtStr)
	if cTime.IsZero() {
		cTime, _ = time.Parse(time.RFC3339, lastCheckedAtStr)
	}

	return &domain.StoredPage{
		Summary: domain.StoredPageSummary{
			Slug:          slug,
			Label:         label,
			Title:         title,
			SourceURL:     sourceURL,
			FetchedAt:     fTime,
			LastCheckedAt: cTime,
			TextCount:     textCount,
			Generation:    generation,
		},
		Snapshot: snapshot,
	}
}

// GetCards retrieves indicator cards associated with a page.
func (s *Store) GetCards(pageSlug string) []domain.IndexCard {
	s.mu.RLock()
	defer s.mu.RUnlock()

	rows, err := s.db.Query(`
		SELECT code, label, value, detail, period, description
		FROM cards WHERE page_slug = ? ORDER BY seq
	`, pageSlug)
	if err != nil {
		return []domain.IndexCard{}
	}
	defer rows.Close()

	var cards []domain.IndexCard
	for rows.Next() {
		var (
			code, label, value, desc string
			detail, period           sql.NullString
		)
		if err := rows.Scan(&code, &label, &value, &detail, &period, &desc); err == nil {
			var d, p *string
			if detail.Valid && detail.String != "" {
				d = &detail.String
			}
			if period.Valid && period.String != "" {
				p = &period.String
			}
			if desc == "" {
				if def, ok := domain.TryGetIndicator(code); ok {
					desc = def.Description
				}
			}
			cards = append(cards, domain.IndexCard{
				Code:        code,
				Label:       label,
				Value:       value,
				Detail:      d,
				Period:      p,
				Description: desc,
			})
		}
	}
	return cards
}

// GetContent retrieves paginated content entries for a page.
func (s *Store) GetContent(pageSlug string, view domain.PageView, offset, limit int) []domain.PageContentItem {
	s.mu.RLock()
	defer s.mu.RUnlock()

	filter := ""
	if view == domain.PageViewTables {
		filter = " AND kind = 'table'"
	}

	query := fmt.Sprintf(`
		SELECT id, kind, text, seq FROM content_entries
		WHERE page_slug = ?%s
		ORDER BY seq LIMIT ? OFFSET ?
	`, filter)

	rows, err := s.db.Query(query, pageSlug, limit, offset)
	if err != nil {
		return []domain.PageContentItem{}
	}
	defer rows.Close()

	var items []domain.PageContentItem
	for rows.Next() {
		var (
			id   int64
			kind string
			text string
			seq  int
		)
		if err := rows.Scan(&id, &kind, &text, &seq); err == nil {
			items = append(items, domain.PageContentItem{
				ID:       id,
				Kind:     domain.ContentKind(kind),
				Text:     text,
				Sequence: seq,
			})
		}
	}
	return items
}

// Search searches indexed content using FTS5 Trigram or LIKE fallback.
func (s *Store) Search(query string, pageSlug *string, kind *domain.ContentKind, offset, limit int, mode domain.SearchMode) []domain.SearchHit {
	s.mu.RLock()
	defer s.mu.RUnlock()

	trimmed := strings.TrimSpace(query)
	if trimmed == "" {
		return []domain.SearchHit{}
	}

	var terms []string
	if mode == domain.SearchModePhrase {
		terms = []string{trimmed}
	} else {
		terms = strings.Fields(trimmed)
	}

	if len(terms) == 0 {
		return []domain.SearchHit{}
	}

	allLongEnough := true
	for _, t := range terms {
		if utf8.RuneCountInString(t) < 3 {
			allLongEnough = false
			break
		}
	}

	if allLongEnough {
		ftsHits := s.searchFTS(query, pageSlug, kind, offset, limit, mode)
		if len(ftsHits) > 0 {
			return ftsHits
		}
	}

	return s.searchLike(terms, pageSlug, kind, offset, limit)
}

func (s *Store) searchFTS(query string, pageSlug *string, kind *domain.ContentKind, offset, limit int, mode domain.SearchMode) []domain.SearchHit {
	var ftsQuery string
	if mode == domain.SearchModePhrase {
		ftsQuery = fmt.Sprintf(`"%s"`, strings.ReplaceAll(query, `"`, `""`))
	} else {
		tokens := strings.Fields(query)
		escaped := make([]string, len(tokens))
		for i, tok := range tokens {
			escaped[i] = fmt.Sprintf(`"%s"`, strings.ReplaceAll(tok, `"`, `""`))
		}
		ftsQuery = strings.Join(escaped, " AND ")
	}

	var conditions []string
	var args []interface{}

	conditions = append(conditions, "content_fts MATCH ?")
	args = append(args, ftsQuery)

	if pageSlug != nil && *pageSlug != "" {
		conditions = append(conditions, "p.slug = ? COLLATE NOCASE")
		args = append(args, *pageSlug)
	}
	if kind != nil && *kind != "" {
		conditions = append(conditions, "c.kind = ?")
		args = append(args, string(*kind))
	}

	args = append(args, limit, offset)

	sqlStr := fmt.Sprintf(`
		SELECT c.id, p.slug, p.label, p.source_url, p.fetched_at, c.kind, c.text, bm25(content_fts) AS rank
		FROM content_fts f
		JOIN content_entries c ON f.rowid = c.id
		JOIN pages p ON c.page_slug = p.slug
		WHERE %s
		ORDER BY rank ASC
		LIMIT ? OFFSET ?
	`, strings.Join(conditions, " AND "))

	rows, err := s.db.Query(sqlStr, args...)
	if err != nil {
		return nil
	}
	defer rows.Close()

	var hits []domain.SearchHit
	for rows.Next() {
		var (
			entryID                                     int64
			slug, label, sourceURL, fetchedAtStr, k, tx string
			rank                                        float64
		)
		if err := rows.Scan(&entryID, &slug, &label, &sourceURL, &fetchedAtStr, &k, &tx, &rank); err == nil {
			fTime, _ := time.Parse(time.RFC3339Nano, fetchedAtStr)
			if fTime.IsZero() {
				fTime, _ = time.Parse(time.RFC3339, fetchedAtStr)
			}
			hits = append(hits, domain.SearchHit{
				EntryID:   entryID,
				PageSlug:  slug,
				PageLabel: label,
				SourceURL: sourceURL,
				FetchedAt: fTime,
				Kind:      domain.ContentKind(k),
				Text:      tx,
				Rank:      rank,
			})
		}
	}
	return hits
}

func (s *Store) searchLike(terms []string, pageSlug *string, kind *domain.ContentKind, offset, limit int) []domain.SearchHit {
	var conditions []string
	var args []interface{}

	for _, t := range terms {
		conditions = append(conditions, "c.text LIKE ?")
		args = append(args, "%"+t+"%")
	}

	if pageSlug != nil && *pageSlug != "" {
		conditions = append(conditions, "p.slug = ? COLLATE NOCASE")
		args = append(args, *pageSlug)
	}
	if kind != nil && *kind != "" {
		conditions = append(conditions, "c.kind = ?")
		args = append(args, string(*kind))
	}

	args = append(args, limit, offset)

	sqlStr := fmt.Sprintf(`
		SELECT c.id, p.slug, p.label, p.source_url, p.fetched_at, c.kind, c.text, 0.0 AS rank
		FROM content_entries c
		JOIN pages p ON c.page_slug = p.slug
		WHERE %s
		ORDER BY c.id ASC
		LIMIT ? OFFSET ?
	`, strings.Join(conditions, " AND "))

	rows, err := s.db.Query(sqlStr, args...)
	if err != nil {
		return nil
	}
	defer rows.Close()

	var hits []domain.SearchHit
	for rows.Next() {
		var (
			entryID                                     int64
			slug, label, sourceURL, fetchedAtStr, k, tx string
			rank                                        float64
		)
		if err := rows.Scan(&entryID, &slug, &label, &sourceURL, &fetchedAtStr, &k, &tx, &rank); err == nil {
			fTime, _ := time.Parse(time.RFC3339Nano, fetchedAtStr)
			if fTime.IsZero() {
				fTime, _ = time.Parse(time.RFC3339, fetchedAtStr)
			}
			hits = append(hits, domain.SearchHit{
				EntryID:   entryID,
				PageSlug:  slug,
				PageLabel: label,
				SourceURL: sourceURL,
				FetchedAt: fTime,
				Kind:      domain.ContentKind(k),
				Text:      tx,
				Rank:      rank,
			})
		}
	}
	return hits
}

type preparedEntry struct {
	kind domain.ContentKind
	text string
}

// ApplyPages updates the database with freshly crawled pages in a single atomic transaction.
func (s *Store) ApplyPages(pages []domain.IndexedPage, checkedAt time.Time) (*ApplyPagesResult, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	if len(pages) == 0 {
		return &ApplyPagesResult{Generation: s.getGenerationLocked()}, nil
	}

	tx, err := s.db.Begin()
	if err != nil {
		return nil, domain.WrapError(domain.ErrCodeDatabaseError, "Failed to begin transaction", err)
	}
	defer tx.Rollback()

	currentGen := s.getGenerationTx(tx)
	checkedAtStr := checkedAt.Format(time.RFC3339Nano)

	type prepPage struct {
		page        domain.IndexedPage
		hash        string
		entries     []preparedEntry
		cards       []domain.IndexCard
		hasExisting bool
		oldHash     string
	}

	prepared := make([]prepPage, len(pages))
	anyChanged := false
	changedCount := 0

	for i, p := range pages {
		h := computeSemanticHash(&p.Crawled.Snapshot)
		entries := buildEntries(&p.Crawled.Snapshot)
		cards := extractCards(&p.Crawled.Snapshot)

		var oldHash string
		err := tx.QueryRow("SELECT content_hash FROM pages WHERE slug = ?", p.Slug).Scan(&oldHash)
		hasExisting := err == nil

		isChanged := !hasExisting || oldHash != h
		if isChanged {
			anyChanged = true
			changedCount++
		}

		prepared[i] = prepPage{
			page:        p,
			hash:        h,
			entries:     entries,
			cards:       cards,
			hasExisting: hasExisting,
			oldHash:     oldHash,
		}
	}

	nextGen := currentGen
	if anyChanged {
		nextGen = currentGen + 1
	}

	for _, pr := range prepared {
		isChanged := !pr.hasExisting || pr.oldHash != pr.hash
		if isChanged {
			// Replace page
			if err := s.replacePageTx(tx, pr.page, pr.hash, pr.entries, pr.cards, checkedAtStr, nextGen); err != nil {
				return nil, err
			}
		} else {
			// Update last_checked_at and source_url
			_, err := tx.Exec(`
				UPDATE pages SET last_checked_at = ?, source_url = ?, label = ?, title = ?
				WHERE slug = ?
			`, checkedAtStr, pr.page.Crawled.Snapshot.SourceURL, pr.page.Label, pr.page.Crawled.Snapshot.Title, pr.page.Slug)
			if err != nil {
				return nil, domain.WrapError(domain.ErrCodeDatabaseError, "Failed to update unchanged page", err)
			}
		}
	}

	if anyChanged {
		_, err = tx.Exec("INSERT OR REPLACE INTO meta (key, value) VALUES ('index_generation', ?)", strconv.FormatInt(nextGen, 10))
		if err != nil {
			return nil, domain.WrapError(domain.ErrCodeDatabaseError, "Failed to update index generation", err)
		}
	}

	if err := tx.Commit(); err != nil {
		return nil, domain.WrapError(domain.ErrCodeDatabaseError, "Failed to commit ApplyPages transaction", err)
	}

	return &ApplyPagesResult{
		ChangedPages:   changedCount,
		UnchangedPages: len(pages) - changedCount,
		Generation:     nextGen,
	}, nil
}

func (s *Store) replacePageTx(
	tx *sql.Tx,
	page domain.IndexedPage,
	contentHash string,
	entries []preparedEntry,
	cards []domain.IndexCard,
	checkedAtStr string,
	generation int64,
) error {
	// 1. Delete existing
	_, _ = tx.Exec("DELETE FROM content_fts WHERE page_slug = ?", page.Slug)
	_, _ = tx.Exec("DELETE FROM content_entries WHERE page_slug = ?", page.Slug)
	_, _ = tx.Exec("DELETE FROM cards WHERE page_slug = ?", page.Slug)
	_, _ = tx.Exec("DELETE FROM pages WHERE slug = ?", page.Slug)

	snapshotBytes, _ := json.Marshal(page.Crawled.Snapshot)
	rawDocsBytes, _ := json.Marshal(page.Crawled.HtmlDocuments)
	fetchedAtStr := page.Crawled.Snapshot.FetchedAt.Format(time.RFC3339Nano)

	// 2. Insert into pages
	_, err := tx.Exec(`
		INSERT INTO pages (slug, label, title, source_url, fetched_at, last_checked_at, snapshot_json, raw_documents_json, text_count, generation, content_hash)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
	`, page.Slug, page.Label, page.Crawled.Snapshot.Title, page.Crawled.Snapshot.SourceURL, fetchedAtStr, checkedAtStr, string(snapshotBytes), string(rawDocsBytes), len(page.Crawled.Snapshot.Text), generation, contentHash)
	if err != nil {
		return domain.WrapError(domain.ErrCodeDatabaseError, "Failed to insert page", err)
	}

	// 3. Insert cards
	cardStmt, err := tx.Prepare(`
		INSERT INTO cards (page_slug, code, label, value, detail, period, description, seq)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?)
	`)
	if err != nil {
		return domain.WrapError(domain.ErrCodeDatabaseError, "Failed to prepare card statement", err)
	}
	defer cardStmt.Close()

	for seq, c := range cards {
		var detail, period sql.NullString
		if c.Detail != nil {
			detail = sql.NullString{String: *c.Detail, Valid: true}
		}
		if c.Period != nil {
			period = sql.NullString{String: *c.Period, Valid: true}
		}
		if _, err := cardStmt.Exec(page.Slug, c.Code, c.Label, c.Value, detail, period, c.Description, seq); err != nil {
			return domain.WrapError(domain.ErrCodeDatabaseError, "Failed to insert card", err)
		}
	}

	// 4. Insert content_entries and content_fts
	entryStmt, err := tx.Prepare(`
		INSERT INTO content_entries (page_slug, kind, text, seq)
		VALUES (?, ?, ?, ?)
	`)
	if err != nil {
		return domain.WrapError(domain.ErrCodeDatabaseError, "Failed to prepare entry statement", err)
	}
	defer entryStmt.Close()

	ftsStmt, err := tx.Prepare(`
		INSERT INTO content_fts (rowid, page_slug, kind, text)
		VALUES (?, ?, ?, ?)
	`)
	if err != nil {
		return domain.WrapError(domain.ErrCodeDatabaseError, "Failed to prepare fts statement", err)
	}
	defer ftsStmt.Close()

	for seq, ent := range entries {
		res, err := entryStmt.Exec(page.Slug, string(ent.kind), ent.text, seq)
		if err != nil {
			return domain.WrapError(domain.ErrCodeDatabaseError, "Failed to insert content entry", err)
		}
		rowID, _ := res.LastInsertId()
		if _, err := ftsStmt.Exec(rowID, page.Slug, string(ent.kind), ent.text); err != nil {
			return domain.WrapError(domain.ErrCodeDatabaseError, "Failed to insert fts entry", err)
		}
	}

	return nil
}

// RecordRefresh records refresh attempt metadata in a single transaction.
func (s *Store) RecordRefresh(finishedAt time.Time, failures []domain.CrawlFailure, fullSuccess bool) {
	s.mu.Lock()
	defer s.mu.Unlock()

	tx, err := s.db.Begin()
	if err != nil {
		return
	}
	defer tx.Rollback()

	finishedAtStr := finishedAt.Format(time.RFC3339Nano)
	_, _ = tx.Exec("INSERT OR REPLACE INTO meta (key, value) VALUES ('last_refresh_attempt', ?)", finishedAtStr)
	_, _ = tx.Exec("INSERT OR REPLACE INTO meta (key, value) VALUES ('last_failure_count', ?)", strconv.Itoa(len(failures)))

	var errMsg string
	if len(failures) > 0 {
		var parts []string
		for _, f := range failures {
			parts = append(parts, fmt.Sprintf("%s: %s: %s", f.URL, f.Code, f.Message))
		}
		errMsg = strings.Join(parts, "; ")
		if len(errMsg) > 4096 {
			errMsg = errMsg[:4096]
		}
	}
	_, _ = tx.Exec("INSERT OR REPLACE INTO meta (key, value) VALUES ('last_error', ?)", errMsg)

	if fullSuccess {
		_, _ = tx.Exec("INSERT OR REPLACE INTO meta (key, value) VALUES ('last_successful_refresh', ?)", finishedAtStr)
	}

	_ = tx.Commit()
}

func (s *Store) getGenerationLocked() int64 {
	var val string
	err := s.db.QueryRow("SELECT value FROM meta WHERE key = 'index_generation'").Scan(&val)
	if err != nil || val == "" {
		return 0
	}
	gen, _ := strconv.ParseInt(val, 10, 64)
	return gen
}

func (s *Store) getGenerationTx(tx *sql.Tx) int64 {
	var val string
	err := tx.QueryRow("SELECT value FROM meta WHERE key = 'index_generation'").Scan(&val)
	if err != nil || val == "" {
		return 0
	}
	gen, _ := strconv.ParseInt(val, 10, 64)
	return gen
}

func buildEntries(s *domain.PageSnapshot) []preparedEntry {
	var entries []preparedEntry
	for _, h := range s.Headings {
		entries = append(entries, preparedEntry{kind: domain.ContentHeading, text: h.Text})
	}
	for _, m := range s.Metrics {
		txt := m.Label
		if m.Value != "" {
			txt = fmt.Sprintf("%s: %s", m.Label, m.Value)
		}
		entries = append(entries, preparedEntry{kind: domain.ContentMetric, text: txt})
	}
	for _, c := range s.Cards {
		entries = append(entries, preparedEntry{kind: domain.ContentMetric, text: fmt.Sprintf("%s %s %s", c.Code, c.Label, c.Value)})
	}
	for _, t := range s.Tables {
		if t.Title != nil && *t.Title != "" {
			entries = append(entries, preparedEntry{kind: domain.ContentTable, text: *t.Title})
		}
		if len(t.Headers) > 0 {
			entries = append(entries, preparedEntry{kind: domain.ContentTable, text: strings.Join(t.Headers, " | ")})
		}
		for _, row := range t.Rows {
			if len(row) > 0 {
				entries = append(entries, preparedEntry{kind: domain.ContentTable, text: strings.Join(row, " | ")})
			}
		}
	}
	for _, ch := range s.Charts {
		if ch.Title != "" {
			entries = append(entries, preparedEntry{kind: domain.ContentChart, text: ch.Title})
		}
		if ch.Description != "" {
			entries = append(entries, preparedEntry{kind: domain.ContentChart, text: ch.Description})
		}
		for _, note := range ch.Notes {
			if note != "" {
				entries = append(entries, preparedEntry{kind: domain.ContentChart, text: note})
			}
		}
	}
	for _, txt := range s.Text {
		if txt != "" {
			entries = append(entries, preparedEntry{kind: domain.ContentText, text: txt})
		}
	}
	return entries
}

func extractCards(s *domain.PageSnapshot) []domain.IndexCard {
	if len(s.Cards) > 0 {
		return s.Cards
	}
	return []domain.IndexCard{}
}
