package store

import (
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"strconv"
	"strings"

	"github.com/Star-Trails/mceindex-mcp/internal/domain"
	_ "modernc.org/sqlite"
)

const CurrentSchemaVersion = 4

// initializeDatabase runs migrations or initial schema creation.
func initializeDatabase(db *sql.DB) error {
	var metaTableExists int
	err := db.QueryRow("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='meta'").Scan(&metaTableExists)
	if err != nil {
		return err
	}

	if metaTableExists == 0 {
		return createSchemaV4(db)
	}

	var versionStr string
	err = db.QueryRow("SELECT value FROM meta WHERE key = 'schema_version'").Scan(&versionStr)
	if err != nil && err != sql.ErrNoRows {
		return err
	}

	var version int
	if versionStr != "" {
		version, _ = strconv.Atoi(versionStr)
	}

	if version < CurrentSchemaVersion {
		return migrateToV4(db, version)
	}

	return nil
}

func createSchemaV4(db *sql.DB) error {
	ddl := `
	CREATE TABLE IF NOT EXISTS pages (
		slug TEXT PRIMARY KEY,
		label TEXT NOT NULL,
		title TEXT NOT NULL,
		source_url TEXT NOT NULL UNIQUE,
		fetched_at TEXT NOT NULL,
		last_checked_at TEXT NOT NULL,
		snapshot_json TEXT NOT NULL,
		raw_documents_json TEXT NOT NULL,
		text_count INTEGER NOT NULL,
		generation INTEGER NOT NULL DEFAULT 1,
		content_hash TEXT NOT NULL DEFAULT ''
	);

	CREATE TABLE IF NOT EXISTS cards (
		page_slug TEXT NOT NULL REFERENCES pages(slug) ON DELETE CASCADE,
		code TEXT NOT NULL,
		label TEXT NOT NULL,
		value TEXT NOT NULL,
		detail TEXT,
		period TEXT,
		description TEXT NOT NULL DEFAULT '',
		seq INTEGER NOT NULL,
		PRIMARY KEY (page_slug, code)
	);

	CREATE TABLE IF NOT EXISTS content_entries (
		id INTEGER PRIMARY KEY AUTOINCREMENT,
		page_slug TEXT NOT NULL REFERENCES pages(slug) ON DELETE CASCADE,
		kind TEXT NOT NULL,
		text TEXT NOT NULL,
		seq INTEGER NOT NULL
	);

	CREATE VIRTUAL TABLE IF NOT EXISTS content_fts USING fts5 (
		page_slug UNINDEXED,
		kind UNINDEXED,
		text,
		tokenize='trigram'
	);

	CREATE TABLE IF NOT EXISTS meta (
		key TEXT PRIMARY KEY,
		value TEXT NOT NULL
	);

	CREATE INDEX IF NOT EXISTS idx_cards_page_seq ON cards(page_slug, seq);
	CREATE INDEX IF NOT EXISTS idx_content_page_seq ON content_entries(page_slug, seq);
	CREATE INDEX IF NOT EXISTS idx_content_page_kind_seq ON content_entries(page_slug, kind, seq);
	`
	_, err := db.Exec(ddl)
	if err != nil {
		return err
	}

	_, err = db.Exec("INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', ?), ('index_generation', '0')", strconv.Itoa(CurrentSchemaVersion))
	return err
}

func migrateToV4(db *sql.DB, fromVersion int) error {
	tx, err := db.Begin()
	if err != nil {
		return err
	}
	defer tx.Rollback()

	// Check if last_checked_at exists in pages
	var hasLastChecked int
	_ = tx.QueryRow("SELECT COUNT(*) FROM pragma_table_info('pages') WHERE name='last_checked_at'").Scan(&hasLastChecked)
	if hasLastChecked == 0 {
		if _, err := tx.Exec("ALTER TABLE pages ADD COLUMN last_checked_at TEXT NOT NULL DEFAULT ''"); err != nil {
			return err
		}
		if _, err := tx.Exec("UPDATE pages SET last_checked_at = fetched_at"); err != nil {
			return err
		}
	}

	// Check if generation exists in pages
	var hasGeneration int
	_ = tx.QueryRow("SELECT COUNT(*) FROM pragma_table_info('pages') WHERE name='generation'").Scan(&hasGeneration)
	if hasGeneration == 0 {
		if _, err := tx.Exec("ALTER TABLE pages ADD COLUMN generation INTEGER NOT NULL DEFAULT 1"); err != nil {
			return err
		}
	}

	// Check if content_hash exists in pages
	var hasHash int
	_ = tx.QueryRow("SELECT COUNT(*) FROM pragma_table_info('pages') WHERE name='content_hash'").Scan(&hasHash)
	if hasHash == 0 {
		if _, err := tx.Exec("ALTER TABLE pages ADD COLUMN content_hash TEXT NOT NULL DEFAULT ''"); err != nil {
			return err
		}
	}

	// Check if period exists in cards
	var hasPeriod int
	_ = tx.QueryRow("SELECT COUNT(*) FROM pragma_table_info('cards') WHERE name='period'").Scan(&hasPeriod)
	if hasPeriod == 0 {
		if _, err := tx.Exec("ALTER TABLE cards ADD COLUMN period TEXT"); err != nil {
			return err
		}
	}

	// Check if description exists in cards
	var hasDesc int
	_ = tx.QueryRow("SELECT COUNT(*) FROM pragma_table_info('cards') WHERE name='description'").Scan(&hasDesc)
	if hasDesc == 0 {
		if _, err := tx.Exec("ALTER TABLE cards ADD COLUMN description TEXT NOT NULL DEFAULT ''"); err != nil {
			return err
		}
	}

	// Recompute hashes and descriptions for existing pages
	rows, err := tx.Query("SELECT slug, snapshot_json FROM pages")
	if err == nil {
		type pageRow struct {
			slug string
			json string
		}
		var pRows []pageRow
		for rows.Next() {
			var pr pageRow
			if err := rows.Scan(&pr.slug, &pr.json); err == nil {
				pRows = append(pRows, pr)
			}
		}
		rows.Close()

		for _, pr := range pRows {
			var snapshot domain.PageSnapshot
			if err := json.Unmarshal([]byte(pr.json), &snapshot); err == nil {
				h := computeSemanticHash(&snapshot)
				_, _ = tx.Exec("UPDATE pages SET content_hash = ? WHERE slug = ?", h, pr.slug)
			}
		}
	}

	// Update meta
	_, err = tx.Exec("INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', ?)", strconv.Itoa(CurrentSchemaVersion))
	if err != nil {
		return err
	}

	return tx.Commit()
}

func computeSemanticHash(s *domain.PageSnapshot) string {
	h := sha256.New()
	write := func(str string) {
		h.Write([]byte(str))
		h.Write([]byte{0})
	}

	write(s.Title)
	if s.Description != nil {
		write(*s.Description)
	}
	if s.AppTitle != nil {
		write(*s.AppTitle)
	}

	for _, hd := range s.Headings {
		write(fmt.Sprintf("%d:%s", hd.Level, hd.Text))
	}
	for _, nav := range s.Navigation {
		u := ""
		if nav.URL != nil {
			u = *nav.URL
		}
		write(fmt.Sprintf("%s:%s:%s", nav.Kind, nav.Text, u))
	}
	for _, m := range s.Metrics {
		d := ""
		if m.Delta != nil {
			d = *m.Delta
		}
		write(fmt.Sprintf("%s:%s:%s", m.Label, m.Value, d))
	}
	for _, t := range s.Tables {
		title := ""
		if t.Title != nil {
			title = *t.Title
		}
		write(title)
		write(strings.Join(t.Headers, "|"))
		for _, row := range t.Rows {
			write(strings.Join(row, "|"))
		}
	}
	for _, c := range s.Cards {
		p := ""
		if c.Period != nil {
			p = *c.Period
		}
		write(fmt.Sprintf("%s:%s:%s:%s", c.Code, c.Label, c.Value, p))
	}
	for _, ch := range s.Charts {
		write(ch.Title)
		for _, ser := range ch.Series {
			n := ""
			if ser.Name != nil {
				n = *ser.Name
			}
			t := ""
			if ser.Type != nil {
				t = *ser.Type
			}
			write(fmt.Sprintf("%s:%s:%d", n, t, len(ser.Points)))
		}
	}
	for _, txt := range s.Text {
		write(txt)
	}

	return hex.EncodeToString(h.Sum(nil))
}
