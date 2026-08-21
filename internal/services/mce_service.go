package services

import (
	"context"
	"strings"
	"sync"

	"github.com/Star-Trails/mceindex-mcp/internal/domain"
	"github.com/Star-Trails/mceindex-mcp/internal/projectors"
	"github.com/Star-Trails/mceindex-mcp/internal/store"
)

// MceIndexService provides high-level business methods for querying and discovering MCEIndex data.
type MceIndexService struct {
	store       *store.Store
	coordinator *RefreshCoordinator
	initOnce    sync.Once
	initErr     error
}

// NewMceIndexService creates a new MceIndexService instance.
func NewMceIndexService(st *store.Store, coord *RefreshCoordinator) *MceIndexService {
	return &MceIndexService{
		store:       st,
		coordinator: coord,
	}
}

// EnsureAvailable guarantees that the local index has been refreshed at least once, falling back to cache.
func (s *MceIndexService) EnsureAvailable(ctx context.Context) error {
	s.initOnce.Do(func() {
		if s.coordinator.ShouldRefresh() {
			_, err := s.coordinator.Refresh(context.Background(), true)
			if err != nil && s.store.CountPages() == 0 {
				s.initErr = err
			}
		}
	})

	if s.initErr != nil && s.store.CountPages() == 0 {
		return s.initErr
	}

	if s.store.CountPages() == 0 {
		return domain.NewError(
			domain.ErrCodeIndexEmpty,
			"No MCEIndex pages are available after the session refresh. Call refresh_index after the hard cooldown to retry.",
		)
	}

	return nil
}

// GetLatest returns the latest monthly overview readings, trends, and verifications.
func (s *MceIndexService) GetLatest(ctx context.Context) (*domain.LatestOverview, error) {
	if err := s.EnsureAvailable(ctx); err != nil {
		return nil, err
	}

	overview := s.store.FindPage("Monthly_Overview")
	if overview == nil {
		overview = s.store.FindPage("月度总览")
	}
	if overview == nil {
		return nil, domain.NewError(domain.ErrCodeIndexEmpty, "The local index does not contain the monthly overview.")
	}

	cards := s.store.GetCards(overview.Summary.Slug)
	evidencePages := s.getEvidencePages()
	sections := projectors.BuildOverviewSections(cards, overview.Snapshot.Charts, evidencePages)

	for i := range sections {
		trend := projectors.BuildIndicatorTrend(sections[i].Code, evidencePages, 13)
		sections[i].Trend = trend
	}

	headings := make([]string, len(overview.Snapshot.Headings))
	for i, h := range overview.Snapshot.Headings {
		headings[i] = h.Text
	}

	var notes []string
	for _, txt := range overview.Snapshot.Text {
		if len(txt) >= 20 {
			notes = append(notes, txt)
			if len(notes) >= 12 {
				break
			}
		}
	}

	return &domain.LatestOverview{
		SourceURL:  overview.Summary.SourceURL,
		FetchedAt:  overview.Summary.FetchedAt,
		Generation: s.coordinator.GetStatus().Generation,
		AtAGlance:  sections,
		Cards:      cards,
		Headings:   headings,
		Notes:      notes,
	}, nil
}

// Discover returns the discovery tree of topics, current readings, and tool recommendations.
func (s *MceIndexService) Discover(ctx context.Context) (*domain.DataDiscoveryResult, error) {
	latest, err := s.GetLatest(ctx)
	if err != nil {
		return nil, err
	}

	pages := s.store.ListPages()
	res := projectors.BuildDataDiscovery(latest, pages)
	return &res, nil
}

// GetIndicator returns single indicator details and customizable history window.
func (s *MceIndexService) GetIndicator(ctx context.Context, indicator string, months int) (*domain.IndicatorResult, error) {
	trimmed := strings.TrimSpace(indicator)
	if trimmed == "" || len(trimmed) > 100 {
		return nil, domain.NewInvalidConfigError("Indicator must be a code or Chinese label between 1 and 100 characters.")
	}
	if months < 2 || months > 120 {
		return nil, domain.NewInvalidConfigError("History window must be between 2 and 120 months.")
	}

	latest, err := s.GetLatest(ctx)
	if err != nil {
		return nil, err
	}

	var foundCard *domain.IndexCard
	for _, c := range latest.Cards {
		if strings.EqualFold(c.Code, trimmed) || strings.EqualFold(c.Label, trimmed) {
			foundCard = &c
			break
		}
	}

	if foundCard == nil {
		available := make([]string, len(latest.Cards))
		for i, c := range latest.Cards {
			available[i] = c.Code
		}
		return nil, domain.NewIndicatorNotFoundError(trimmed, available)
	}

	evidencePages := s.getEvidencePages()
	trend := projectors.BuildIndicatorTrend(foundCard.Code, evidencePages, months)

	return &domain.IndicatorResult{
		Indicator:  *foundCard,
		SourceURL:  latest.SourceURL,
		FetchedAt:  latest.FetchedAt,
		Generation: latest.Generation,
		Trend:      trend,
	}, nil
}

// ListPages lists all indexed pages and index refresh status.
func (s *MceIndexService) ListPages(ctx context.Context) (*domain.PageListResult, error) {
	if err := s.EnsureAvailable(ctx); err != nil {
		return nil, err
	}

	return &domain.PageListResult{
		Status: s.coordinator.GetStatus(),
		Pages:  s.store.ListPages(),
	}, nil
}

// GetPage retrieves page summary, content, tables, or sanitized charts.
func (s *MceIndexService) GetPage(ctx context.Context, page string, view domain.PageView, offset, limit int) (*domain.PageResult, error) {
	trimmed := strings.TrimSpace(page)
	if trimmed == "" || len(trimmed) > 200 || offset < 0 || offset > 10_000 || limit < 1 || limit > 100 {
		return nil, domain.NewInvalidConfigError("Page must be 1-200 characters; offset must be 0-10000 and limit must be 1-100.")
	}

	if err := s.EnsureAvailable(ctx); err != nil {
		return nil, err
	}

	stored := s.findPage(trimmed)
	if stored == nil {
		pages := s.store.ListPages()
		available := make([]string, len(pages))
		for i, p := range pages {
			available[i] = p.Slug
		}
		return nil, domain.NewPageNotFoundError(trimmed, available)
	}

	cards := s.store.GetCards(stored.Summary.Slug)

	if view == domain.PageViewSummary {
		return &domain.PageResult{
			Page:       stored.Summary,
			View:       view,
			Cards:      cards,
			Headings:   stored.Snapshot.Headings,
			Metrics:    stored.Snapshot.Metrics,
			Items:      []domain.PageContentItem{},
			Tables:     []domain.DataTable{},
			Charts:     []domain.ChartData{},
			Offset:     0,
			NextOffset: nil,
			HasMore:    false,
		}, nil
	}

	if view == domain.PageViewTables {
		tbls := stored.Snapshot.Tables
		var window []domain.DataTable
		if offset < len(tbls) {
			end := offset + limit + 1
			if end > len(tbls) {
				end = len(tbls)
			}
			window = tbls[offset:end]
		}
		hasMore := len(window) > limit
		resTbls := window
		if hasMore {
			resTbls = window[:limit]
		}
		var nextOffset *int
		if hasMore {
			n := offset + limit
			nextOffset = &n
		}
		return &domain.PageResult{
			Page:       stored.Summary,
			View:       view,
			Cards:      cards,
			Headings:   stored.Snapshot.Headings,
			Metrics:    stored.Snapshot.Metrics,
			Items:      []domain.PageContentItem{},
			Tables:     resTbls,
			Charts:     []domain.ChartData{},
			Offset:     offset,
			NextOffset: nextOffset,
			HasMore:    hasMore,
		}, nil
	}

	if view == domain.PageViewCharts {
		charts := stored.Snapshot.Charts
		var window []domain.ChartData
		if offset < len(charts) {
			end := offset + limit + 1
			if end > len(charts) {
				end = len(charts)
			}
			window = charts[offset:end]
		}
		hasMore := len(window) > limit
		resCharts := window
		if hasMore {
			resCharts = window[:limit]
		}
		var nextOffset *int
		if hasMore {
			n := offset + limit
			nextOffset = &n
		}
		projected := projectors.ProjectCharts(resCharts)
		return &domain.PageResult{
			Page:       stored.Summary,
			View:       view,
			Cards:      []domain.IndexCard{},
			Headings:   stored.Snapshot.Headings,
			Metrics:    stored.Snapshot.Metrics,
			Items:      []domain.PageContentItem{},
			Tables:     []domain.DataTable{},
			Charts:     projected,
			Offset:     offset,
			NextOffset: nextOffset,
			HasMore:    hasMore,
		}, nil
	}

	// Default View: Content
	entries := s.store.GetContent(stored.Summary.Slug, view, offset, limit+1)
	hasMore := len(entries) > limit
	resEntries := entries
	if hasMore {
		resEntries = entries[:limit]
	}
	var nextOffset *int
	if hasMore {
		n := offset + limit
		nextOffset = &n
	}

	return &domain.PageResult{
		Page:       stored.Summary,
		View:       view,
		Cards:      cards,
		Headings:   stored.Snapshot.Headings,
		Metrics:    stored.Snapshot.Metrics,
		Items:      resEntries,
		Tables:     []domain.DataTable{},
		Charts:     []domain.ChartData{},
		Offset:     offset,
		NextOffset: nextOffset,
		HasMore:    hasMore,
	}, nil
}

// Search executes FTS5 or LIKE query against indexed text.
func (s *MceIndexService) Search(ctx context.Context, query string, page *string, kind *domain.ContentKind, mode domain.SearchMode, offset, limit int) (*domain.SearchResult, error) {
	trimmed := strings.TrimSpace(query)
	if trimmed == "" {
		return nil, domain.NewInvalidConfigError("Search query must not be empty.")
	}
	if len(trimmed) > 500 || offset < 0 || offset > 10_000 || limit < 1 || limit > 50 {
		return nil, domain.NewInvalidConfigError("Search query must be at most 500 characters; offset must be 0-10000 and limit must be 1-50.")
	}

	if err := s.EnsureAvailable(ctx); err != nil {
		return nil, err
	}

	var pageSlug *string
	if page != nil && strings.TrimSpace(*page) != "" {
		stored := s.findPage(strings.TrimSpace(*page))
		if stored == nil {
			pages := s.store.ListPages()
			available := make([]string, len(pages))
			for i, p := range pages {
				available[i] = p.Slug
			}
			return nil, domain.NewPageNotFoundError(*page, available)
		}
		pageSlug = &stored.Summary.Slug
	}

	hits := s.store.Search(trimmed, pageSlug, kind, offset, limit+1, mode)
	hasMore := len(hits) > limit
	resHits := hits
	if hasMore {
		resHits = hits[:limit]
	}
	var nextOffset *int
	if hasMore {
		n := offset + limit
		nextOffset = &n
	}

	return &domain.SearchResult{
		Query:      trimmed,
		Page:       pageSlug,
		Kind:       kind,
		Hits:       resHits,
		Offset:     offset,
		NextOffset: nextOffset,
		HasMore:    hasMore,
		Generation: s.coordinator.GetStatus().Generation,
	}, nil
}

// Refresh explicitly invokes the coordinator refresh.
func (s *MceIndexService) Refresh(ctx context.Context, force bool) (*domain.RefreshResult, error) {
	report, err := s.coordinator.Refresh(ctx, force)
	if err != nil {
		return nil, err
	}
	return &domain.RefreshResult{
		Report: *report,
		Status: s.coordinator.GetStatus(),
	}, nil
}

func (s *MceIndexService) getEvidencePages() map[string]*domain.StoredPage {
	slugs := []string{
		"LI_Monthly",
		"Meaningful_Retail",
		"Meaningful_CPI_PPI",
		"Meaningful_TSF",
	}
	res := make(map[string]*domain.StoredPage, len(slugs))
	for _, slug := range slugs {
		if p := s.store.FindPage(slug); p != nil {
			res[slug] = p
		}
	}
	return res
}

func (s *MceIndexService) findPage(query string) *domain.StoredPage {
	return s.store.FindPage(query)
}
