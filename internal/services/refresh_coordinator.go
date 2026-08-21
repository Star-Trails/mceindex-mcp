package services

import (
	"context"
	"net/url"
	"strings"
	"sync"
	"time"

	"github.com/Star-Trails/mceindex-mcp/internal/config"
	"github.com/Star-Trails/mceindex-mcp/internal/crawling"
	"github.com/Star-Trails/mceindex-mcp/internal/domain"
	"github.com/Star-Trails/mceindex-mcp/internal/store"
)

var productionRoutes = []struct {
	Path  string
	Label string
}{
	{Path: "/Monthly_Overview", Label: "月度总览"},
	{Path: "/LI_Monthly", Label: "五大新产业续命指数"},
	{Path: "/Meaningful_CPI_PPI", Label: "有意义CPI/PPI"},
	{Path: "/Meaningful_TSF", Label: "有意义社融"},
	{Path: "/Meaningful_Retail", Label: "有意义社零"},
}

const forcedRefreshMinimumInterval = 1 * time.Minute

type crawlTarget struct {
	URL   *url.URL
	Label string
}

type crawlAttempt struct {
	Target  crawlTarget
	Crawled *domain.CrawledPage
	Failure *domain.CrawlFailure
}

// RefreshCoordinator coordinates crawling waves, concurrency limits, and refresh caching intervals.
type RefreshCoordinator struct {
	options       *config.Options
	store         *store.Store
	crawler       crawling.Crawler
	mu            sync.Mutex
	activeRefresh chan *domain.CrawlReport
	activeErr     chan error
	disposed      bool
}

// NewRefreshCoordinator creates a new RefreshCoordinator instance.
func NewRefreshCoordinator(opts *config.Options, st *store.Store, cr crawling.Crawler) *RefreshCoordinator {
	return &RefreshCoordinator{
		options: opts,
		store:   st,
		crawler: cr,
	}
}

// IsRefreshing checks if a crawl refresh is actively running.
func (rc *RefreshCoordinator) IsRefreshing() bool {
	rc.mu.Lock()
	defer rc.mu.Unlock()
	return rc.activeRefresh != nil
}

// IsStale checks if the current index is older than the configured refresh interval.
func (rc *RefreshCoordinator) IsStale() bool {
	val, ok := rc.store.GetMeta("last_successful_refresh")
	if !ok || val == "" {
		return true
	}
	t, err := time.Parse(time.RFC3339Nano, val)
	if err != nil {
		t, err = time.Parse(time.RFC3339, val)
	}
	if err != nil {
		return true
	}
	return time.Since(t) > rc.options.RefreshInterval
}

// ShouldRefresh determines whether a background refresh is warranted.
func (rc *RefreshCoordinator) ShouldRefresh() bool {
	lastAttemptVal, ok := rc.store.GetMeta("last_refresh_attempt")
	if ok && lastAttemptVal != "" {
		t, err := time.Parse(time.RFC3339Nano, lastAttemptVal)
		if err == nil && time.Since(t) < rc.options.RefreshInterval {
			return false
		}
	}

	if rc.store.CountPages() == 0 {
		return true
	}

	return rc.IsStale()
}

// GetStatus returns the current IndexStatus summary.
func (rc *RefreshCoordinator) GetStatus() domain.IndexStatus {
	var lastSuccess *time.Time
	if val, ok := rc.store.GetMeta("last_successful_refresh"); ok && val != "" {
		if t, err := time.Parse(time.RFC3339Nano, val); err == nil {
			lastSuccess = &t
		}
	}

	var lastAttempt *time.Time
	if val, ok := rc.store.GetMeta("last_refresh_attempt"); ok && val != "" {
		if t, err := time.Parse(time.RFC3339Nano, val); err == nil {
			lastAttempt = &t
		}
	}

	var lastError *string
	if val, ok := rc.store.GetMeta("last_error"); ok && val != "" {
		lastError = &val
	}

	return domain.IndexStatus{
		DatabasePath:          rc.store.Path(),
		SchemaVersion:         store.CurrentSchemaVersion,
		PageCount:             rc.store.CountPages(),
		Generation:            rc.store.GetGeneration(),
		LastSuccessfulRefresh: lastSuccess,
		LastAttempt:           lastAttempt,
		Stale:                 rc.IsStale(),
		Refreshing:            rc.IsRefreshing(),
		LastError:             lastError,
	}
}

// Refresh triggers an index refresh wave, joining active refreshes if already in progress.
func (rc *RefreshCoordinator) Refresh(ctx context.Context, force bool) (*domain.CrawlReport, error) {
	rc.mu.Lock()
	if rc.disposed {
		rc.mu.Unlock()
		return nil, domain.NewError(domain.ErrCodeInternalError, "RefreshCoordinator is disposed.")
	}

	if rc.activeRefresh != nil {
		reportCh := rc.activeRefresh
		errCh := rc.activeErr
		rc.mu.Unlock()

		select {
		case r := <-reportCh:
			return r, nil
		case err := <-errCh:
			return nil, err
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}

	if rc.shouldSkipRefresh(force) {
		now := time.Now().UTC()
		rc.mu.Unlock()
		return &domain.CrawlReport{
			StartedAt:      now,
			FinishedAt:     now,
			Outcome:        domain.RefreshSkipped,
			PagesChecked:   0,
			ChangedPages:   0,
			UnchangedPages: 0,
			Failures:       []domain.CrawlFailure{},
		}, nil
	}

	reportCh := make(chan *domain.CrawlReport, 1)
	errCh := make(chan error, 1)
	rc.activeRefresh = reportCh
	rc.activeErr = errCh
	rc.mu.Unlock()

	go func() {
		defer func() {
			_ = rc.crawler.CloseBrowser()
			rc.mu.Lock()
			rc.activeRefresh = nil
			rc.activeErr = nil
			rc.mu.Unlock()
		}()

		report, err := rc.crawlAll(context.Background())
		if err != nil {
			errCh <- err
		} else {
			reportCh <- report
		}
	}()

	select {
	case r := <-reportCh:
		return r, nil
	case err := <-errCh:
		return nil, err
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}

func (rc *RefreshCoordinator) shouldSkipRefresh(force bool) bool {
	if !force {
		return !rc.ShouldRefresh()
	}

	val, ok := rc.store.GetMeta("last_refresh_attempt")
	if ok && val != "" {
		t, err := time.Parse(time.RFC3339Nano, val)
		if err == nil && time.Since(t) < forcedRefreshMinimumInterval {
			return true
		}
	}
	return false
}

func (rc *RefreshCoordinator) crawlAll(ctx context.Context) (*domain.CrawlReport, error) {
	startedAt := time.Now().UTC()
	targets := rc.initialTargets()
	seen := make(map[string]struct{})
	var successful []crawlAttempt
	var failures []domain.CrawlFailure

	cursor := 0
	for cursor < len(targets) && len(seen) < rc.options.MaxPages {
		var wave []crawlTarget
		for cursor < len(targets) && len(seen) < rc.options.MaxPages {
			t := targets[cursor]
			cursor++
			norm := t.URL.String()
			if _, exists := seen[norm]; !exists {
				seen[norm] = struct{}{}
				wave = append(wave, t)
			}
		}

		sem := make(chan struct{}, rc.options.CrawlConcurrency)
		var waveMu sync.Mutex
		var wg sync.WaitGroup

		for _, target := range wave {
			wg.Add(1)
			go func(tgt crawlTarget) {
				defer wg.Done()
				sem <- struct{}{}
				defer func() { <-sem }()

				attempt := rc.crawlWithRetry(ctx, tgt)
				waveMu.Lock()
				defer waveMu.Unlock()

				if attempt.Crawled == nil {
					if attempt.Failure != nil {
						failures = append(failures, *attempt.Failure)
					}
					return
				}

				successful = append(successful, attempt)
				for _, nav := range attempt.Crawled.Snapshot.Navigation {
					if nav.URL == nil {
						continue
					}
					discURL, err := url.Parse(*nav.URL)
					if err != nil || !sameOrigin(rc.options.BaseURL, discURL) {
						continue
					}
					if _, alreadySeen := seen[discURL.String()]; !alreadySeen {
						targets = append(targets, crawlTarget{
							URL:   discURL,
							Label: nav.Text,
						})
					}
				}
			}(target)
		}

		wg.Wait()
	}

	indexedMap := make(map[string]domain.IndexedPage)
	for _, res := range successful {
		source, _ := url.Parse(res.Crawled.Snapshot.SourceURL)
		slug := strings.Trim(source.Path, "/")
		if slug == "" {
			slug = "home"
		}
		label := res.Target.Label
		for _, nav := range res.Crawled.Snapshot.Navigation {
			if nav.URL != nil {
				if itemU, err := url.Parse(*nav.URL); err == nil && itemU.Path == source.Path {
					label = nav.Text
					break
				}
			}
		}
		indexedMap[strings.ToLower(slug)] = domain.IndexedPage{
			Slug:    slug,
			Label:   label,
			Crawled: *res.Crawled,
		}
	}

	indexed := make([]domain.IndexedPage, 0, len(indexedMap))
	for _, p := range indexedMap {
		indexed = append(indexed, p)
	}

	finishedAt := time.Now().UTC()
	applied, err := rc.store.ApplyPages(indexed, finishedAt)
	if err != nil {
		return nil, err
	}

	fullSuccess := len(failures) == 0 && len(indexed) > 0
	rc.store.RecordRefresh(finishedAt, failures, fullSuccess)

	if len(indexed) == 0 && rc.store.CountPages() == 0 {
		return nil, domain.NewError(
			domain.ErrCodeIndexEmpty,
			"The initial MCEIndex crawl failed for every page.",
			map[string]interface{}{"failures": failures},
		)
	}

	outcome := domain.RefreshCompleted
	if len(failures) > 0 {
		outcome = domain.RefreshPartial
	}

	return &domain.CrawlReport{
		StartedAt:      startedAt,
		FinishedAt:     finishedAt,
		Outcome:        outcome,
		PagesChecked:   len(indexed),
		ChangedPages:   applied.ChangedPages,
		UnchangedPages: applied.UnchangedPages,
		Failures:       failures,
	}, nil
}

func (rc *RefreshCoordinator) crawlWithRetry(ctx context.Context, target crawlTarget) crawlAttempt {
	var lastErr error
	for attempt := 1; attempt <= 3; attempt++ {
		crawled, err := rc.crawler.Crawl(ctx, target.URL)
		if err == nil {
			return crawlAttempt{
				Target:  target,
				Crawled: crawled,
			}
		}
		lastErr = err
		if !isRetryable(err) || attempt == 3 {
			break
		}
		select {
		case <-time.After(time.Duration(attempt) * time.Second):
		case <-ctx.Done():
			return crawlAttempt{
				Target: target,
				Failure: &domain.CrawlFailure{
					URL:     target.URL.String(),
					Code:    "ACQUISITION_CANCELLED",
					Message: ctx.Err().Error(),
				},
			}
		}
	}

	code := "ACQUISITION_FAILED"
	msg := "MCEIndex acquisition failed."
	if domainErr, ok := lastErr.(*domain.MceIndexError); ok {
		code = string(domainErr.Code)
		msg = domainErr.Message
	} else if lastErr != nil {
		msg = lastErr.Error()
	}

	return crawlAttempt{
		Target: target,
		Failure: &domain.CrawlFailure{
			URL:     target.URL.String(),
			Code:    code,
			Message: msg,
		},
	}
}

func (rc *RefreshCoordinator) initialTargets() []crawlTarget {
	isProd := strings.EqualFold(rc.options.BaseURL.Hostname(), "mceindex.com") ||
		strings.HasSuffix(strings.ToLower(rc.options.BaseURL.Hostname()), ".mceindex.com")

	if isProd {
		targets := make([]crawlTarget, len(productionRoutes))
		for i, r := range productionRoutes {
			u, _ := rc.options.BaseURL.Parse(r.Path)
			targets[i] = crawlTarget{
				URL:   u,
				Label: r.Label,
			}
		}
		return targets
	}

	return []crawlTarget{
		{
			URL:   rc.options.BaseURL,
			Label: "月度总览",
		},
	}
}

func isRetryable(err error) bool {
	if domainErr, ok := err.(*domain.MceIndexError); ok {
		return domainErr.Code == domain.ErrCodeLoadTimeout || domainErr.Code == domain.ErrCodeExtractionFailed
	}
	return true
}

func sameOrigin(u1, u2 *url.URL) bool {
	return strings.EqualFold(u1.Scheme, u2.Scheme) &&
		strings.EqualFold(u1.Hostname(), u2.Hostname()) &&
		u1.Port() == u2.Port()
}

// Close gracefully closes the coordinator and underlying crawler.
func (rc *RefreshCoordinator) Close() error {
	rc.mu.Lock()
	defer rc.mu.Unlock()

	rc.disposed = true
	return rc.crawler.Close()
}
