package crawling

import (
	"context"
	"encoding/json"
	"fmt"
	"net/url"
	"strings"
	"sync"
	"time"

	"github.com/Star-Trails/mceindex-mcp/internal/config"
	"github.com/Star-Trails/mceindex-mcp/internal/domain"
	"github.com/Star-Trails/mceindex-mcp/internal/parsing"
	"github.com/go-rod/rod"
)

const (
	maxCharts           = 32
	maxTotalChartPoints = 100_000
)

var (
	lifeIndexViews = []string{
		"产业规模占比", "直接就业能力", "净财政能力", "行业下钻", "方法来源",
	}
	lifeIndexIndustries = []string{
		"集成电路", "新能源汽车", "新能源", "电气化设备", "医药制造",
	}

	productionChartMinimums = map[string]struct {
		Initial  int
		Captured int
		Points   int
	}{
		"/Monthly_Overview":   {Initial: 6, Captured: 6, Points: 45},
		"/LI_Monthly":         {Initial: 1, Captured: 23, Points: 1_470},
		"/Meaningful_CPI_PPI": {Initial: 3, Captured: 3, Points: 1_159},
		"/Meaningful_TSF":     {Initial: 2, Captured: 2, Points: 144},
		"/Meaningful_Retail":  {Initial: 2, Captured: 2, Points: 150},
	}
)

// Crawler defines the interface for crawling MCEIndex pages.
type Crawler interface {
	Crawl(ctx context.Context, target *url.URL) (*domain.CrawledPage, error)
	CloseBrowser() error
	Close() error
}

// MceCrawler implements Crawler using headless browser automation.
type MceCrawler struct {
	options       *config.Options
	parser        *parsing.Parser
	browserRunner *BrowserRunner
	requestMu     sync.Mutex
	nextRequestAt time.Time
	disposed      bool
}

// NewCrawler creates a new MceCrawler.
func NewCrawler(opts *config.Options, p *parsing.Parser, runner *BrowserRunner) *MceCrawler {
	return &MceCrawler{
		options:       opts,
		parser:        p,
		browserRunner: runner,
	}
}

// Crawl navigates to target URL, waits for Streamlit hydration, and extracts content and charts.
func (c *MceCrawler) Crawl(ctx context.Context, target *url.URL) (*domain.CrawledPage, error) {
	if c.disposed {
		return nil, domain.NewError(domain.ErrCodeInternalError, "Crawler is closed.")
	}

	c.waitForRequestSlot(ctx)

	page, err := c.browserRunner.OpenStealthPage(ctx)
	if err != nil {
		return nil, err
	}
	defer page.Close()

	if err := c.navigateAndHydrate(ctx, page, target); err != nil {
		return nil, err
	}

	documents := make([]string, 0, parsing.MaxHtmlDocuments)
	totalChars, err := c.captureDocuments(page, &documents, 0)
	if err != nil {
		return nil, err
	}

	charts, err := c.extractCharts(page)
	if err != nil {
		return nil, err
	}

	// Select All history if available
	allChanged, err := c.selectAllHistory(page)
	if err != nil {
		return nil, err
	}
	if allChanged {
		totalChars, err = c.captureDocuments(page, &documents, totalChars)
		if err != nil {
			return nil, err
		}
		historyCharts, err := c.extractCharts(page)
		if err != nil {
			return nil, err
		}
		charts = mergeCharts(charts, historyCharts)
	}

	source := c.getSource(page, target)
	normPath := strings.TrimRight(source.Path, "/")
	if strings.HasSuffix(strings.ToLower(normPath), "/li_monthly") {
		charts, err = c.captureLifeIndexViews(page, &documents, totalChars, charts)
		if err != nil {
			return nil, err
		}
	}

	if err := validateProductionChartCoverage(source, charts); err != nil {
		return nil, err
	}

	snapshot, err := c.parser.Extract(documents, source, time.Now().UTC())
	if err != nil {
		return nil, err
	}
	snapshot.Charts = charts

	if len(snapshot.Headings) == 0 ||
		(len(snapshot.Metrics) == 0 && len(snapshot.Tables) == 0 &&
			len(snapshot.Cards) == 0 && len(snapshot.Charts) == 0 && len(snapshot.Text) == 0) {
		return nil, domain.NewError(
			domain.ErrCodeExtractionFailed,
			fmt.Sprintf("MCEIndex returned no complete page content for %s.", source.String()),
		)
	}

	return &domain.CrawledPage{
		Snapshot:      *snapshot,
		HtmlDocuments: documents,
	}, nil
}

func (c *MceCrawler) CloseBrowser() error {
	return c.browserRunner.Close()
}

func (c *MceCrawler) Close() error {
	c.disposed = true
	return c.browserRunner.Close()
}

func (c *MceCrawler) waitForRequestSlot(ctx context.Context) {
	c.requestMu.Lock()
	defer c.requestMu.Unlock()

	now := time.Now()
	if c.nextRequestAt.After(now) {
		select {
		case <-time.After(c.nextRequestAt.Sub(now)):
		case <-ctx.Done():
			return
		}
	}
	c.nextRequestAt = time.Now().Add(c.options.CrawlDelay)
}

func (c *MceCrawler) navigateAndHydrate(ctx context.Context, page *rod.Page, target *url.URL) error {
	err := page.Timeout(c.options.RequestTimeout).Navigate(target.String())
	if err != nil {
		return domain.WrapError(domain.ErrCodeLoadTimeout, "Navigation failed", err)
	}

	expectedCharts := 0
	if isCanonicalMceIndex(target) {
		if min, ok := productionChartMinimums[strings.TrimRight(target.Path, "/")]; ok {
			expectedCharts = min.Initial
		}
	}

	readyExpr := fmt.Sprintf(`
		async () => {
			const sleep = ms => new Promise(r => setTimeout(r, ms));
			const deadline = Date.now() + %d;
			while (Date.now() < deadline) {
				const main = document.querySelector("[data-testid='stMain'], main");
				const heading = main?.querySelector("h1")?.innerText.trim();
				const charts = document.querySelectorAll("[data-testid='stPlotlyChart'] .js-plotly-plot").length;
				if (heading && charts >= %d) return { ready: true, html: document.documentElement.outerHTML };
				await sleep(200);
			}
			return { ready: false, html: document.documentElement.outerHTML };
		}
	`, c.options.RequestTimeout.Milliseconds(), expectedCharts)
	val, err := page.Eval(readyExpr)
	if err != nil {
		return domain.WrapError(domain.ErrCodeLoadTimeout, "Error evaluating ready expression", err)
	}

	var res struct {
		Ready bool   `json:"ready"`
		HTML  string `json:"html"`
	}
	_ = json.Unmarshal([]byte(val.Value.JSON("", "")), &res)

	if parsing.IsAccessChallenge(res.HTML) {
		return domain.NewError(domain.ErrCodeAccessChallenge, "Cloudflare verification blocked MCEIndex acquisition.")
	}

	if !res.Ready {
		return domain.NewError(
			domain.ErrCodeLoadTimeout,
			fmt.Sprintf("MCEIndex did not become ready within %dms.", c.options.RequestTimeout.Milliseconds()),
		)
	}

	c.waitForDomQuiet(page)
	c.scrollLazyContent(page)
	return nil
}

func (c *MceCrawler) captureDocuments(page *rod.Page, documents *[]string, totalChars int) (int, error) {
	remainingDocs := parsing.MaxHtmlDocuments - len(*documents)
	remainingChars := parsing.MaxTotalHtmlCharacters - totalChars
	if remainingDocs <= 0 || remainingChars <= 0 {
		return totalChars, domain.NewError(domain.ErrCodeExtractionFailed, "MCEIndex HTML exceeded safe extraction limits.")
	}

	expr := fmt.Sprintf(`
		() => {
			const documents = [document.documentElement.outerHTML];
			for (const frame of document.querySelectorAll("iframe")) {
				try {
					const html = frame.contentDocument?.documentElement?.outerHTML;
					if (html) documents.push(html);
				} catch {}
			}
			if (documents.length > %d || documents.reduce((sum, v) => sum + v.length, 0) > %d) {
				return { limitExceeded: true, documents: [] };
			}
			return { limitExceeded: false, documents };
		}
	`, remainingDocs, remainingChars)

	val, err := page.Eval(expr)
	if err != nil {
		return totalChars, domain.WrapError(domain.ErrCodeExtractionFailed, "Failed to capture HTML documents", err)
	}

	var res struct {
		LimitExceeded bool     `json:"limitExceeded"`
		Documents     []string `json:"documents"`
	}
	_ = json.Unmarshal([]byte(val.Value.JSON("", "")), &res)

	if res.LimitExceeded {
		return totalChars, domain.NewError(domain.ErrCodeExtractionFailed, "MCEIndex HTML exceeded safe extraction limits.")
	}

	for _, doc := range res.Documents {
		if parsing.IsAccessChallenge(doc) {
			return totalChars, domain.NewError(domain.ErrCodeAccessChallenge, "Cloudflare verification blocked MCEIndex acquisition.")
		}
		var err error
		totalChars, err = parsingIncludeDocument(doc, totalChars)
		if err != nil {
			return totalChars, err
		}
		*documents = append(*documents, doc)
	}

	return totalChars, nil
}

func parsingIncludeDocument(htmlStr string, totalChars int) (int, error) {
	if len(htmlStr) > parsing.MaxHtmlDocumentCharacters || totalChars > parsing.MaxTotalHtmlCharacters-len(htmlStr) {
		return totalChars, domain.NewError(domain.ErrCodeExtractionFailed, "MCEIndex HTML exceeded safe extraction limits.")
	}
	return totalChars + len(htmlStr), nil
}

func (c *MceCrawler) extractCharts(page *rod.Page) ([]domain.ChartData, error) {
	val, err := page.Eval(chartExtractionExpression)
	if err != nil {
		return nil, domain.WrapError(domain.ErrCodeExtractionFailed, "Failed to extract charts", err)
	}

	if val.Value.Str() == "__MCEINDEX_RESOURCE_LIMIT__" {
		return nil, domain.NewError(domain.ErrCodeExtractionFailed, "MCEIndex chart data exceeded safe extraction limits.")
	}

	var charts []domain.ChartData
	if err := json.Unmarshal([]byte(val.Value.JSON("", "")), &charts); err != nil {
		return []domain.ChartData{}, nil
	}
	return charts, nil
}

func (c *MceCrawler) selectAllHistory(page *rod.Page) (bool, error) {
	expr := fmt.Sprintf(`
		async () => {
			const sleep = ms => new Promise(r => setTimeout(r, ms));
			const deadline = Date.now() + %d;
			let changed = false;
			while (Date.now() < deadline) {
				const control = [...document.querySelectorAll("button")].find(btn =>
					btn.innerText.trim() === "All" && btn.getClientRects().length > 0 &&
					btn.getAttribute("data-testid") !== "stBaseButton-segmented_controlActive");
				if (!control) break;
				changed = true;
				control.click();
				await sleep(%d);
			}
			const inactive = [...document.querySelectorAll("button")].some(btn =>
				btn.innerText.trim() === "All" && btn.getClientRects().length > 0 &&
				btn.getAttribute("data-testid") !== "stBaseButton-segmented_controlActive");
			if (inactive) return { changed, complete: false };
			await sleep(%d);
			return { changed, complete: true };
		}
	`, c.options.RequestTimeout.Milliseconds(), c.options.DomQuietPeriod.Milliseconds(), c.options.DomQuietPeriod.Milliseconds())

	val, err := page.Eval(expr)
	if err != nil {
		return false, domain.WrapError(domain.ErrCodeExtractionFailed, "Failed to toggle All history", err)
	}

	var res struct {
		Changed  bool `json:"changed"`
		Complete bool `json:"complete"`
	}
	_ = json.Unmarshal([]byte(val.Value.JSON("", "")), &res)

	if !res.Complete {
		return false, domain.NewError(domain.ErrCodeExtractionFailed, "MCEIndex did not activate every visible All-history control.")
	}

	if res.Changed {
		c.waitForChartsStable(page)
	}
	return res.Changed, nil
}

func (c *MceCrawler) captureLifeIndexViews(
	page *rod.Page,
	documents *[]string,
	totalChars int,
	initialCharts []domain.ChartData,
) ([]domain.ChartData, error) {
	activeView, err := c.getActiveView(page)
	if err != nil {
		return nil, err
	}

	charts := make([]domain.ChartData, 0, 32)
	if activeView == "行业下钻" {
		industry, err := c.getSelectedIndustry(page)
		if err != nil {
			return nil, err
		}
		charts = append(charts, labelCharts(fmt.Sprintf("行业下钻 / %s", industry), initialCharts)...)
		totalChars, charts, err = c.captureOtherIndustries(page, documents, totalChars, charts, industry)
		if err != nil {
			return nil, err
		}
	} else {
		charts = append(charts, labelCharts(activeView, initialCharts)...)
	}

	for _, view := range lifeIndexViews {
		if view == activeView {
			continue
		}
		if err := c.selectView(page, view); err != nil {
			return nil, err
		}
		c.assertNoChallenge(page)

		var err error
		totalChars, err = c.captureDocuments(page, documents, totalChars)
		if err != nil {
			return nil, err
		}

		viewCharts, err := c.extractCharts(page)
		if err != nil {
			return nil, err
		}

		if view == "行业下钻" {
			industry, err := c.getSelectedIndustry(page)
			if err != nil {
				return nil, err
			}
			charts = append(charts, labelCharts(fmt.Sprintf("行业下钻 / %s", industry), viewCharts)...)
			totalChars, charts, err = c.captureOtherIndustries(page, documents, totalChars, charts, industry)
			if err != nil {
				return nil, err
			}
		} else {
			charts = append(charts, labelCharts(view, viewCharts)...)
		}
	}

	if err := validateResourceLimits(charts); err != nil {
		return nil, err
	}
	return charts, nil
}

func (c *MceCrawler) captureOtherIndustries(
	page *rod.Page,
	documents *[]string,
	totalChars int,
	charts []domain.ChartData,
	selectedIndustry string,
) (int, []domain.ChartData, error) {
	for _, ind := range lifeIndexIndustries {
		if ind == selectedIndustry {
			continue
		}
		if err := c.selectIndustry(page, ind); err != nil {
			return totalChars, charts, err
		}
		var err error
		totalChars, err = c.captureDocuments(page, documents, totalChars)
		if err != nil {
			return totalChars, charts, err
		}
		indCharts, err := c.extractCharts(page)
		if err != nil {
			return totalChars, charts, err
		}
		charts = append(charts, labelCharts(fmt.Sprintf("行业下钻 / %s", ind), indCharts)...)
	}
	return totalChars, charts, nil
}

func (c *MceCrawler) getActiveView(page *rod.Page) (string, error) {
	viewsJSON, _ := json.Marshal(lifeIndexViews)
	expr := fmt.Sprintf(`() => [...document.querySelectorAll("button[data-testid='stBaseButton-segmented_controlActive']")].map(btn => btn.innerText.trim()).find(text => %s.includes(text)) || null`, string(viewsJSON))

	val, err := page.Eval(expr)
	if err != nil || val.Value.Str() == "" {
		return "", domain.NewError(domain.ErrCodeExtractionFailed, "MCEIndex did not expose the expected life-index view selector.")
	}
	return val.Value.Str(), nil
}

func (c *MceCrawler) selectView(page *rod.Page, view string) error {
	viewJSON, _ := json.Marshal(view)
	expr := fmt.Sprintf(`
		async () => {
			const target = %s;
			const sleep = ms => new Promise(r => setTimeout(r, ms));
			const button = [...document.querySelectorAll("button")].find(btn => btn.innerText.trim() === target && btn.getClientRects().length > 0);
			if (!button) return false;
			if (button.getAttribute("data-testid") !== "stBaseButton-segmented_controlActive") button.click();
			const deadline = Date.now() + %d;
			while (Date.now() < deadline) {
				const active = [...document.querySelectorAll("button[data-testid='stBaseButton-segmented_controlActive']")]
					.some(btn => btn.innerText.trim() === target);
				if (active) { await sleep(%d); return true; }
				await sleep(100);
			}
			return false;
		}
	`, string(viewJSON), c.options.RequestTimeout.Milliseconds(), c.options.DomQuietPeriod.Milliseconds())

	val, err := page.Eval(expr)
	if err != nil || !val.Value.Bool() {
		return domain.NewError(domain.ErrCodeExtractionFailed, fmt.Sprintf("MCEIndex life-index view “%s” was not available.", view))
	}
	c.scrollLazyContent(page)
	return nil
}

func (c *MceCrawler) getSelectedIndustry(page *rod.Page) (string, error) {
	val, err := page.Eval(`() => document.querySelector("[data-testid='stSelectbox'] [value]")?.getAttribute('value') || null`)
	if err != nil || val.Value.Str() == "" {
		return "", domain.NewError(domain.ErrCodeExtractionFailed, "MCEIndex did not expose a supported selected industry.")
	}
	ind := val.Value.Str()

	found := false
	for _, target := range lifeIndexIndustries {
		if target == ind {
			found = true
			break
		}
	}
	if !found {
		return "", domain.NewError(domain.ErrCodeExtractionFailed, "MCEIndex did not expose a supported selected industry.")
	}
	return ind, nil
}

func (c *MceCrawler) selectIndustry(page *rod.Page, industry string) error {
	indJSON, _ := json.Marshal(industry)
	expr := fmt.Sprintf(`
		async () => {
			const target = %s;
			const sleep = ms => new Promise(r => setTimeout(r, ms));
			const select = document.querySelector("[data-testid='stSelectbox'] [role='combobox']");
			if (!select) return false;
			select.click();
			const optionDeadline = Date.now() + %d;
			let option;
			while (Date.now() < optionDeadline && !option) {
				option = [...document.querySelectorAll("[role='option']")].find(cand => cand.innerText.trim() === target);
				if (!option) await sleep(100);
			}
			if (!option) return false;
			option.click();
			while (Date.now() < optionDeadline) {
				if (document.querySelector("[data-testid='stSelectbox'] [value]")?.getAttribute("value") === target) {
					await sleep(%d);
					return true;
				}
				await sleep(100);
			}
			return false;
		}
	`, string(indJSON), c.options.RequestTimeout.Milliseconds(), c.options.DomQuietPeriod.Milliseconds())

	val, err := page.Eval(expr)
	if err != nil || !val.Value.Bool() {
		return domain.NewError(domain.ErrCodeExtractionFailed, fmt.Sprintf("MCEIndex industry “%s” was not available.", industry))
	}
	c.scrollLazyContent(page)
	return nil
}

func (c *MceCrawler) scrollLazyContent(page *rod.Page) {
	expr := fmt.Sprintf(`
		async () => {
			const sleep = ms => new Promise(r => setTimeout(r, ms));
			for (let offset = 0; offset < document.documentElement.scrollHeight; offset += 700) {
				window.scrollTo(0, offset);
				await sleep(80);
			}
			window.scrollTo(0, 0);
			await sleep(%d);
			return true;
		}
	`, c.options.DomQuietPeriod.Milliseconds())
	_, _ = page.Eval(expr)
}

func (c *MceCrawler) waitForChartsStable(page *rod.Page) {
	expr := fmt.Sprintf(`
		async () => {
			const sleep = ms => new Promise(r => setTimeout(r, ms));
			const signature = () => [...document.querySelectorAll("[data-testid='stPlotlyChart'] .js-plotly-plot")].map(plot =>
				(plot._fullData?.length ? plot._fullData : (plot.data || [])).map(trace =>
					Math.max(trace.x?.length || 0, trace.y?.length || 0, trace.labels?.length || 0, trace.values?.length || 0)).join(",")).join(";");
			const deadline = Date.now() + %d;
			let previous = signature();
			let unchangedSince = Date.now();
			while (Date.now() < deadline) {
				await sleep(200);
				const current = signature();
				if (current !== previous) { previous = current; unchangedSince = Date.now(); }
				if (current && Date.now() - unchangedSince >= %d) return true;
			}
			return false;
		}
	`, c.options.RequestTimeout.Milliseconds(), c.options.DomQuietPeriod.Milliseconds())
	_, _ = page.Eval(expr)
}

func (c *MceCrawler) waitForDomQuiet(page *rod.Page) {
	expr := fmt.Sprintf(`
		() => new Promise(resolve => {
			const root = document.querySelector("[data-testid='stMain'], main");
			if (!root) { resolve(false); return; }
			let quietTimer;
			let maxTimer;
			const finish = val => { clearTimeout(quietTimer); clearTimeout(maxTimer); observer.disconnect(); resolve(val); };
			const arm = () => { clearTimeout(quietTimer); quietTimer = setTimeout(() => finish(true), %d); };
			const observer = new MutationObserver(arm);
			observer.observe(root, { subtree: true, childList: true, attributes: true, characterData: true });
			maxTimer = setTimeout(() => finish(false), %d);
			arm();
		})
	`, c.options.DomQuietPeriod.Milliseconds(), c.options.RequestTimeout.Milliseconds())
	_, _ = page.Eval(expr)
}

func (c *MceCrawler) assertNoChallenge(page *rod.Page) {
	val, err := page.Eval("() => document.documentElement.outerHTML")
	if err == nil && parsing.IsAccessChallenge(val.Value.Str()) {
		panic(domain.NewError(domain.ErrCodeAccessChallenge, "Cloudflare verification blocked MCEIndex acquisition."))
	}
}

func (c *MceCrawler) getSource(page *rod.Page, fallback *url.URL) *url.URL {
	val, err := page.Eval("() => location.href")
	if err == nil && val.Value.Str() != "" {
		if u, err := url.Parse(val.Value.Str()); err == nil {
			return u
		}
	}
	return fallback
}

func mergeCharts(initial, allHistory []domain.ChartData) []domain.ChartData {
	merged := make([]domain.ChartData, len(initial))
	copy(merged, initial)
	indices := make(map[string]int, len(merged))
	for i, ch := range merged {
		indices[chartKey(ch)] = i
	}

	for _, ch := range allHistory {
		k := chartKey(ch)
		idx, exists := indices[k]
		if !exists {
			indices[k] = len(merged)
			merged = append(merged, ch)
		} else {
			existingPoints := 0
			for _, s := range merged[idx].Series {
				existingPoints += len(s.Points)
			}
			newPoints := 0
			for _, s := range ch.Series {
				newPoints += len(s.Points)
			}
			if newPoints > existingPoints {
				merged[idx] = ch
			}
		}
	}
	return merged
}

func chartKey(ch domain.ChartData) string {
	var b strings.Builder
	b.WriteString(ch.Title)
	b.WriteString("\x1f")
	for i, s := range ch.Series {
		if i > 0 {
			b.WriteString("\x1e")
		}
		n := ""
		if s.Name != nil {
			n = *s.Name
		}
		t := ""
		if s.Type != nil {
			t = *s.Type
		}
		b.WriteString(fmt.Sprintf("%s\x1d%s", n, t))
	}
	return b.String()
}

func validateProductionChartCoverage(source *url.URL, charts []domain.ChartData) error {
	if !isCanonicalMceIndex(source) {
		return nil
	}
	normPath := strings.TrimRight(source.Path, "/")
	min, ok := productionChartMinimums[normPath]
	if !ok {
		return nil
	}

	points := 0
	for _, ch := range charts {
		for _, s := range ch.Series {
			points += len(s.Points)
		}
	}

	if len(charts) >= min.Captured && points >= min.Points {
		return nil
	}

	return domain.NewError(
		domain.ErrCodeExtractionFailed,
		fmt.Sprintf("MCEIndex returned incomplete chart coverage for %s: expected at least %d charts and %d points, received %d charts and %d points.",
			source.Path, min.Captured, min.Points, len(charts), points),
	)
}

func isCanonicalMceIndex(u *url.URL) bool {
	return strings.EqualFold(u.Hostname(), "mceindex.com") || strings.HasSuffix(strings.ToLower(u.Hostname()), ".mceindex.com")
}

func labelCharts(view string, charts []domain.ChartData) []domain.ChartData {
	labeled := make([]domain.ChartData, len(charts))
	for i, ch := range charts {
		generic := strings.TrimSpace(ch.Title) == "" || strings.HasPrefix(ch.Title, "<") || strings.HasPrefix(ch.Title, "图表 ")
		title := ch.Title
		if generic {
			title = fmt.Sprintf("图表 %d", i+1)
		}
		desc := ch.Description
		if generic {
			desc = fmt.Sprintf("MCEIndex“%s”视图中的图表。", view)
		}
		labeled[i] = domain.ChartData{
			Title:       fmt.Sprintf("%s · %s", view, title),
			Description: desc,
			Notes:       ch.Notes,
			XAxisTitle:  ch.XAxisTitle,
			YAxisTitle:  ch.YAxisTitle,
			Series:      ch.Series,
		}
	}
	return labeled
}

func validateResourceLimits(charts []domain.ChartData) error {
	if len(charts) > maxCharts {
		return domain.NewError(domain.ErrCodeExtractionFailed, "MCEIndex chart data exceeded safe extraction limits.")
	}
	totalPoints := 0
	for _, ch := range charts {
		for _, s := range ch.Series {
			totalPoints += len(s.Points)
		}
	}
	if totalPoints > maxTotalChartPoints {
		return domain.NewError(domain.ErrCodeExtractionFailed, "MCEIndex chart data exceeded safe extraction limits.")
	}
	return nil
}

const chartExtractionExpression = `
() => {
  const MAX_CHARTS = 32, MAX_SERIES_PER_CHART = 32, MAX_POINTS_PER_SERIES = 10000;
  const MAX_TOTAL_POINTS = 100000, MAX_BINARY_CHARACTERS = 1000000;
  let extractionLimitExceeded = false, totalPoints = 0;
  const valuesOf = input => {
    if (input == null) return [];
    if (Array.isArray(input) || ArrayBuffer.isView(input)) {
      if (input.length > MAX_POINTS_PER_SERIES) extractionLimitExceeded = true;
      return Array.from({ length: Math.min(input.length, MAX_POINTS_PER_SERIES) }, (_, index) => input[index]);
    }
    if (typeof input !== "object" || typeof input.bdata !== "string") return [input];
    if (input.bdata.length > MAX_BINARY_CHARACTERS) { extractionLimitExceeded = true; return []; }
    const binary = atob(input.bdata);
    const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
    const dtype = String(input.dtype || "f8").replace(/[<>=|]/g, "");
    const constructors = { f4: Float32Array, f8: Float64Array, i1: Int8Array, i2: Int16Array, i4: Int32Array, u1: Uint8Array, u2: Uint16Array, u4: Uint32Array };
    const TypedArray = constructors[dtype];
    if (!TypedArray) return [];
    const values = new TypedArray(bytes.buffer);
    if (values.length > MAX_POINTS_PER_SERIES) extractionLimitExceeded = true;
    return Array.from(values.subarray(0, MAX_POINTS_PER_SERIES));
  };
  const textOf = value => value == null ? null : String(value);
  const plainTextOf = value => {
    const text = textOf(value);
    if (!text) return "";
    const template = document.createElement("template");
    template.innerHTML = text;
    return (template.content.textContent || "").trim();
  };
  const numberOf = value => {
    if (typeof value === "number") return Number.isFinite(value) ? Number(value.toPrecision(15)) : null;
    if (typeof value !== "string" || value.trim() === "") return null;
    const parsed = Number(value);
    return Number.isFinite(parsed) ? Number(parsed.toPrecision(15)) : null;
  };
  const categoryOf = value => {
    const text = plainTextOf(value);
    if (!text) return null;
    const month = /^(\d{4})-(\d{2})(?:-01(?:[T ]00:00:00(?:\.0+)?(?:Z|[+-]00:00)?)?)?$/.exec(text);
    if (month) return month[1] + "-" + month[2];
    if (/^\d{4}-\d{2}-\d{2}[T ]/.test(text)) {
      const timestamp = Date.parse(text);
      if (Number.isFinite(timestamp)) return new Date(timestamp).toISOString();
    }
    return text;
  };
  const headerFor = plot => {
    let cursor = plot.closest("[data-testid='stElementContainer']");
    for (let index = 0; cursor && index < 8; index += 1, cursor = cursor.previousElementSibling) {
      const header = cursor.querySelector?.(".chart-header");
      if (header) return header;
    }
    return null;
  };
  const plots = Array.from(document.querySelectorAll("[data-testid='stPlotlyChart'] .js-plotly-plot"));
  if (plots.length > MAX_CHARTS) return "__MCEINDEX_RESOURCE_LIMIT__";
  const charts = plots.map((plot, chartIndex) => {
    const header = headerFor(plot), layout = plot._fullLayout || plot.layout || {};
    const title = plainTextOf(header?.querySelector("h1,h2,h3,h4,h5,h6")?.innerText || layout.title?.text) ||
      plainTextOf(layout.yaxis?.title?.text) || plainTextOf(layout.xaxis?.title?.text) || ("图表 " + (chartIndex + 1));
    const description = plainTextOf(header?.querySelector(".chart-header-summary")?.innerText) || ("MCEIndex 页面中的“" + title + "”图表。");
    const notes = Array.from(header?.querySelectorAll("p:not(.chart-header-summary)") || []).map(element => element.innerText.trim()).filter(Boolean);
    const traces = Array.from(plot._fullData?.length ? plot._fullData : (plot.data || []));
    if (traces.length > MAX_SERIES_PER_CHART) extractionLimitExceeded = true;
    const series = traces.slice(0, MAX_SERIES_PER_CHART).map(trace => {
      const x = valuesOf(trace.x), y = valuesOf(trace.y), labels = valuesOf(trace.labels), values = valuesOf(trace.values), texts = valuesOf(trace.text);
      const tickValues = valuesOf(layout.yaxis?.tickvals), tickLabels = valuesOf(layout.yaxis?.ticktext);
      const yCategories = tickValues.length === tickLabels.length && tickLabels.length > 0 ? y.map(value => {
        const index = tickValues.findIndex(tick => numberOf(tick) != null && numberOf(value) != null ? numberOf(tick) === numberOf(value) : String(tick) === String(value));
        return index >= 0 ? tickLabels[index] : value;
      }) : y;
      const horizontal = trace.orientation === "h";
      const categories = horizontal ? yCategories : (tickLabels.length > 0 && x.length > 0 ? yCategories : (x.length > 0 ? x : labels));
      const numericValues = horizontal ? x : (tickLabels.length > 0 && x.length > 0 ? x : (y.length > 0 ? y : values));
      const count = Math.max(categories.length, numericValues.length, texts.length);
      if (count > MAX_POINTS_PER_SERIES || totalPoints > MAX_TOTAL_POINTS - count) { extractionLimitExceeded = true; return { name: textOf(trace.name), type: textOf(trace.type), points: [] }; }
      totalPoints += count;
      return { name: plainTextOf(trace.name) || null, type: textOf(trace.type), points: Array.from({ length: count }, (_, index) => {
        const value = numberOf(numericValues[index]), text = plainTextOf(texts[index]) || null;
        return { category: categoryOf(categories[index]), value, text, displayValue: text || (value == null ? null : String(value)) };
      }) };
    }).filter(item => item.points.length > 0);
    return { title, description, notes, xAxisTitle: plainTextOf(layout.xaxis?.title?.text) || null, yAxisTitle: plainTextOf(layout.yaxis?.title?.text) || null, series };
  }).filter(chart => chart.series.length > 0);
  if (extractionLimitExceeded) return "__MCEINDEX_RESOURCE_LIMIT__";
  return charts;
}
`
