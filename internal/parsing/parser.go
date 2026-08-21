package parsing

import (
	"fmt"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"time"

	"github.com/PuerkitoBio/goquery"
	"github.com/Star-Trails/mceindex-mcp/internal/domain"
	"golang.org/x/net/html"
)

const (
	MaxHtmlDocuments          = 32
	MaxHtmlDocumentCharacters = 5_000_000
	MaxTotalHtmlCharacters    = 20_000_000
)

var (
	whitespaceRegex = regexp.MustCompile(`\s+`)
	periodRegex     = regexp.MustCompile(`\b\d{4}-\d{2}\b`)
)

// IsAccessChallenge checks whether the HTML contains Cloudflare challenge markers.
func IsAccessChallenge(htmlStr string) bool {
	lower := strings.ToLower(htmlStr)
	return strings.Contains(lower, "cf-chl-") ||
		strings.Contains(lower, "challenges.cloudflare.com") ||
		strings.Contains(lower, "just a moment...") ||
		strings.Contains(lower, "verify you are human")
}

// Parser parses and extracts structured content from HTML documents.
type Parser struct{}

func NewParser() *Parser {
	return &Parser{}
}

// Extract extracts PageSnapshot from a list of HTML documents.
func (p *Parser) Extract(htmlDocuments []string, sourceURL *url.URL, fetchedAt time.Time) (*domain.PageSnapshot, error) {
	if len(htmlDocuments) == 0 {
		return nil, domain.NewError(domain.ErrCodeExtractionFailed, "No HTML documents were supplied for extraction.")
	}
	if len(htmlDocuments) > MaxHtmlDocuments {
		return nil, domain.NewError(domain.ErrCodeExtractionFailed, fmt.Sprintf("MCEIndex returned more than %d HTML documents.", MaxHtmlDocuments))
	}

	totalCharacters := 0
	for _, doc := range htmlDocuments {
		var err error
		totalCharacters, err = includeDocument(doc, totalCharacters)
		if err != nil {
			return nil, err
		}
	}

	var (
		headings   []domain.Heading
		navigation []domain.NavigationItem
		metrics    []domain.Metric
		tables     []domain.DataTable
		cards      []domain.IndexCard
		textList   []string
		title      string
		desc       *string
	)

	for _, docStr := range htmlDocuments {
		doc, err := goquery.NewDocumentFromReader(strings.NewReader(docStr))
		if err != nil {
			continue
		}

		if title == "" {
			title = normalize(doc.Find("title").Text())
		}
		if desc == nil {
			if metaDesc, exists := doc.Find("meta[name='description']").Attr("content"); exists {
				normDesc := normalize(metaDesc)
				if normDesc != "" {
					desc = &normDesc
				}
			}
		}

		root := doc.Find("[data-testid='stMain']")
		if root.Length() == 0 {
			root = doc.Find("main")
		}
		if root.Length() == 0 {
			root = doc.Find("body")
		}
		if root.Length() == 0 {
			continue
		}

		// 1. Ticker Cards
		root.Find(".terminal-ticker-item").Each(func(_ int, s *goquery.Selection) {
			code := normalize(s.Find(".terminal-ticker-code").Text())
			value := normalize(s.Find(".terminal-ticker-value").Text())
			if value == "" {
				return
			}
			definition, ok := domain.TryGetIndicator(code)
			if !ok {
				return
			}

			detailText := normalize(s.Find(".terminal-ticker-comparison").Text())
			var detail *string
			var period *string
			if detailText != "" {
				detail = &detailText
				p := extractPeriod(&detailText)
				if p != "" {
					period = &p
				}
			}

			cards = append(cards, domain.IndexCard{
				Code:        definition.Code,
				Label:       definition.Label,
				Value:       value,
				Detail:      detail,
				Period:      period,
				Description: definition.Description,
			})
		})

		// Remove non-content elements
		root.Find("script,style,noscript,svg").Remove()

		// 2. Headings
		root.Find("h1,h2,h3,h4,h5,h6").Each(func(_ int, s *goquery.Selection) {
			val := normalize(s.Text())
			if val == "" {
				return
			}
			tagName := strings.ToLower(goquery.NodeName(s))
			if len(tagName) == 2 && tagName[0] == 'h' {
				if lvl, err := strconv.Atoi(tagName[1:]); err == nil {
					headings = append(headings, domain.Heading{
						Level: lvl,
						Text:  val,
					})
				}
			}
		})

		// 3. Navigation
		doc.Find("[data-testid='stSidebar'] a, [data-testid='stSidebar'] button, [data-testid='stSidebarNav'] a, nav a, [role='tab']").Each(func(_ int, s *goquery.Selection) {
			val := s.AttrOr("aria-label", s.Text())
			val = normalize(val)
			if val == "" {
				return
			}

			var targetURL *string
			if href, exists := s.Attr("href"); exists && strings.TrimSpace(href) != "" {
				if resolved, err := sourceURL.Parse(href); err == nil {
					abs := resolved.String()
					targetURL = &abs
				}
			}

			kind := domain.NavKindButton
			tagName := strings.ToLower(goquery.NodeName(s))
			if tagName == "a" {
				kind = domain.NavKindLink
			} else if s.AttrOr("role", "") == "tab" {
				kind = domain.NavKindTab
			}

			navigation = append(navigation, domain.NavigationItem{
				Text: val,
				Kind: kind,
				URL:  targetURL,
			})
		})

		// 4. Metrics
		root.Find("[data-testid='stMetric']").Each(func(_ int, s *goquery.Selection) {
			label := normalize(s.Find("[data-testid='stMetricLabel']").Text())
			value := normalize(s.Find("[data-testid='stMetricValue']").Text())
			if label == "" && value == "" {
				return
			}

			deltaText := normalize(s.Find("[data-testid='stMetricDelta']").Text())
			var delta *string
			if deltaText != "" {
				delta = &deltaText
			}

			helpText := s.AttrOr("title", "")
			if helpText == "" {
				helpText = s.Find("[aria-label]").AttrOr("aria-label", "")
			}
			helpText = normalize(helpText)
			var help *string
			if helpText != "" && helpText != label {
				help = &helpText
			}

			metrics = append(metrics, domain.Metric{
				Label: label,
				Value: value,
				Delta: delta,
				Help:  help,
			})
		})

		// 5. Tables
		root.Find("table").Each(func(_ int, s *goquery.Selection) {
			var headers []string
			s.Find("thead th").Each(func(_ int, th *goquery.Selection) {
				headers = append(headers, normalize(th.Text()))
			})

			var rows [][]string
			s.Find("tbody tr").Each(func(_ int, tr *goquery.Selection) {
				var row []string
				hasVal := false
				tr.Find("th,td").Each(func(_ int, cell *goquery.Selection) {
					v := normalize(cell.Text())
					if v != "" {
						hasVal = true
					}
					row = append(row, v)
				})
				if hasVal {
					rows = append(rows, row)
				}
			})

			if len(headers) == 0 && len(rows) == 0 {
				return
			}

			title := findTableTitle(s)
			tables = append(tables, domain.DataTable{
				Headers: headers,
				Rows:    rows,
				Title:   title,
			})
		})

		// 6. Text
		root.Find("h1,h2,h3,h4,h5,h6,p,li,blockquote,figcaption,[role='alert']").Each(func(_ int, s *goquery.Selection) {
			addNormalized(&textList, s.Text())
		})

		root.Find("div,span,strong,small").Each(func(_ int, s *goquery.Selection) {
			var direct strings.Builder
			for _, node := range s.Nodes {
				for c := node.FirstChild; c != nil; c = c.NextSibling {
					if c.Type == html.TextNode {
						direct.WriteString(c.Data)
					}
				}
			}
			addNormalized(&textList, direct.String())
		})
	}

	uniqueHeadings := unique(headings, func(item domain.Heading) string {
		return fmt.Sprintf("%d:%s", item.Level, item.Text)
	})

	var appTitle *string
	for _, h := range uniqueHeadings {
		if h.Level == 1 {
			t := h.Text
			appTitle = &t
			break
		}
	}

	if title == "" {
		title = "MCEIndex"
	}

	return &domain.PageSnapshot{
		SourceURL:   sourceURL.String(),
		FetchedAt:   fetchedAt,
		Title:       title,
		Description: desc,
		AppTitle:    appTitle,
		Headings:    uniqueHeadings,
		Navigation: unique(navigation, func(item domain.NavigationItem) string {
			u := ""
			if item.URL != nil {
				u = *item.URL
			}
			return fmt.Sprintf("%s:%s:%s", item.Kind, item.Text, u)
		}),
		Metrics: unique(metrics, func(item domain.Metric) string {
			d := ""
			if item.Delta != nil {
				d = *item.Delta
			}
			return fmt.Sprintf("%s:%s:%s", item.Label, item.Value, d)
		}),
		Tables: unique(tables, tableKey),
		Cards: unique(cards, func(item domain.IndexCard) string {
			return item.Code
		}),
		Charts: []domain.ChartData{},
		Text: unique(textList, func(item string) string {
			return item
		}),
	}, nil
}

func includeDocument(htmlStr string, totalChars int) (int, error) {
	if len(htmlStr) > MaxHtmlDocumentCharacters || totalChars > MaxTotalHtmlCharacters-len(htmlStr) {
		return totalChars, domain.NewError(domain.ErrCodeExtractionFailed, "MCEIndex HTML exceeded safe extraction limits.")
	}
	return totalChars + len(htmlStr), nil
}

func findTableTitle(s *goquery.Selection) *string {
	for prev := s.Prev(); prev.Length() > 0; prev = prev.Prev() {
		tagName := strings.ToLower(goquery.NodeName(prev))
		if tagName == "h2" || tagName == "h3" || tagName == "h4" || tagName == "h5" || tagName == "h6" || tagName == "caption" {
			val := normalize(prev.Text())
			if val != "" {
				return &val
			}
		}
	}
	return nil
}

func tableKey(t domain.DataTable) string {
	title := ""
	if t.Title != nil {
		title = *t.Title
	}
	var b strings.Builder
	b.WriteString(title)
	b.WriteString("\x1f")
	b.WriteString(strings.Join(t.Headers, "\x1e"))
	b.WriteString("\x1d")
	for i, row := range t.Rows {
		if i > 0 {
			b.WriteString("\x1c")
		}
		b.WriteString(strings.Join(row, "\x1e"))
	}
	return b.String()
}

func extractPeriod(detail *string) string {
	if detail == nil {
		return ""
	}
	return periodRegex.FindString(*detail)
}

func unique[T any](items []T, keySelector func(T) string) []T {
	seen := make(map[string]struct{}, len(items))
	result := make([]T, 0, len(items))
	for _, item := range items {
		k := keySelector(item)
		if _, exists := seen[k]; !exists {
			seen[k] = struct{}{}
			result = append(result, item)
		}
	}
	return result
}

func addNormalized(values *[]string, val string) {
	norm := normalize(val)
	if norm != "" {
		*values = append(*values, norm)
	}
}

func normalize(val string) string {
	if val == "" {
		return ""
	}
	replaced := strings.ReplaceAll(val, "\u00a0", " ")
	return strings.TrimSpace(whitespaceRegex.ReplaceAllString(replaced, " "))
}
