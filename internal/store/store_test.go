package store

import (
	"database/sql"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/Star-Trails/mceindex-mcp/internal/domain"
	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
	_ "modernc.org/sqlite"
)

func TestStoreAppliesPagesIdempotentlyAndSearchesChinese(t *testing.T) {
	st, err := NewStore(":memory:")
	require.NoError(t, err)
	defer st.Close()

	now := time.Unix(0, 0).UTC()
	firstResult, err := st.ApplyPages([]domain.IndexedPage{
		{
			Slug:    "Monthly_Overview",
			Label:   "月度总览",
			Crawled: makePage("10.54%", now),
		},
	}, now)
	require.NoError(t, err)
	assert.Equal(t, 1, firstResult.ChangedPages)
	assert.Equal(t, 0, firstResult.UnchangedPages)
	assert.Equal(t, int64(1), firstResult.Generation)

	// Unchanged apply
	unchangedResult, err := st.ApplyPages([]domain.IndexedPage{
		{
			Slug:    "Monthly_Overview",
			Label:   "月度总览",
			Crawled: makePage("10.54%", now.Add(time.Hour)),
		},
	}, now.Add(time.Hour))
	require.NoError(t, err)
	assert.Equal(t, 0, unchangedResult.ChangedPages)
	assert.Equal(t, 1, unchangedResult.UnchangedPages)
	assert.Equal(t, int64(1), unchangedResult.Generation)

	// Changed apply
	changedResult, err := st.ApplyPages([]domain.IndexedPage{
		{
			Slug:    "Monthly_Overview",
			Label:   "月度总览",
			Crawled: makePage("10.90%", now.Add(2*time.Hour)),
		},
	}, now.Add(2*time.Hour))
	require.NoError(t, err)
	assert.Equal(t, 1, changedResult.ChangedPages)
	assert.Equal(t, 0, changedResult.UnchangedPages)
	assert.Equal(t, int64(2), changedResult.Generation)

	// Search Chinese phrase
	hits := st.Search("新能源汽车", nil, nil, 0, 10, domain.SearchModePhrase)
	assert.NotEmpty(t, hits)
	assert.Contains(t, hits[0].Text, "新能源汽车")

	// Search Chinese with ContentKind filter
	kindText := domain.ContentText
	hitsText := st.Search("汽车", nil, &kindText, 0, 10, domain.SearchModeAnd)
	assert.NotEmpty(t, hitsText)

	// Search Chart
	kindChart := domain.ContentChart
	hitsChart := st.Search("产业规模", nil, &kindChart, 0, 10, domain.SearchModePhrase)
	assert.NotEmpty(t, hitsChart)

	// Verify Cards
	cards := st.GetCards("Monthly_Overview")
	require.Len(t, cards, 1)
	assert.Equal(t, "LEI-GDP", cards[0].Code)
	assert.Equal(t, "10.90%", cards[0].Value)
	require.NotNil(t, cards[0].Period)
	assert.Equal(t, "2026-06", *cards[0].Period)

	// Verify Page Retrieval
	p := st.FindPage("Monthly_Overview")
	require.NotNil(t, p)
	assert.Equal(t, "月度总览", p.Summary.Label)
	assert.Len(t, p.Snapshot.Charts, 1)
}

func TestStoreMigrationFromLegacyV2(t *testing.T) {
	tempDir, err := os.MkdirTemp("", "mceindex-v2-test")
	require.NoError(t, err)
	defer os.RemoveAll(tempDir)

	dbPath := filepath.Join(tempDir, "legacy.db")

	// Create legacy V2 database
	db, err := sql.Open("sqlite", dbPath)
	require.NoError(t, err)

	legacyDDL := `
	CREATE TABLE pages(slug TEXT PRIMARY KEY,label TEXT NOT NULL,title TEXT NOT NULL,source_url TEXT NOT NULL UNIQUE,
		fetched_at TEXT NOT NULL,snapshot_json TEXT NOT NULL,raw_documents_json TEXT NOT NULL,text_count INTEGER NOT NULL);
	CREATE TABLE cards(page_slug TEXT NOT NULL REFERENCES pages(slug) ON DELETE CASCADE,code TEXT NOT NULL,label TEXT NOT NULL,
		value TEXT NOT NULL,detail TEXT,seq INTEGER NOT NULL,PRIMARY KEY(page_slug,code));
	CREATE TABLE content_entries(id INTEGER PRIMARY KEY,page_slug TEXT NOT NULL REFERENCES pages(slug) ON DELETE CASCADE,
		kind TEXT NOT NULL,text TEXT NOT NULL,seq INTEGER NOT NULL);
	CREATE VIRTUAL TABLE content_fts USING fts5(page_slug UNINDEXED,kind UNINDEXED,text,tokenize='trigram');
	CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
	INSERT INTO pages VALUES('Monthly_Overview','月度总览','有意义中国经济指数','https://mceindex.com/Monthly_Overview',
		'2026-08-10T00:00:00.000Z',
		'{"sourceUrl":"https://mceindex.com/Monthly_Overview","fetchedAt":"2026-08-10T00:00:00Z","title":"有意义中国经济指数","appTitle":"月度总览","headings":[{"level":1,"text":"月度总览"}],"navigation":[],"metrics":[],"tables":[],"text":["新能源汽车产量"]}',
		'["<main>fixture</main>"]',1);
	INSERT INTO content_entries(page_slug,kind,text,seq) VALUES('Monthly_Overview','text','新能源汽车产量',0);
	INSERT INTO content_fts(page_slug,kind,text) VALUES('Monthly_Overview','text','新能源汽车产量');
	INSERT INTO meta VALUES('schema_version','2');
	`
	_, err = db.Exec(legacyDDL)
	require.NoError(t, err)
	db.Close()

	// Open with Store, should auto migrate to V4
	st, err := NewStore(dbPath)
	require.NoError(t, err)
	defer st.Close()

	ver, ok := st.GetMeta("schema_version")
	assert.True(t, ok)
	assert.Equal(t, "4", ver)

	p := st.FindPage("Monthly_Overview")
	require.NotNil(t, p)
	require.NotNil(t, p.Snapshot.AppTitle)
	assert.Equal(t, "月度总览", *p.Snapshot.AppTitle)

	hits := st.Search("新能源汽车", nil, nil, 0, 10, domain.SearchModePhrase)
	assert.NotEmpty(t, hits)
}

func makePage(val string, fetchedAt time.Time) domain.CrawledPage {
	snapshot := domain.PageSnapshot{
		SourceURL: "https://mceindex.com/Monthly_Overview",
		FetchedAt: fetchedAt,
		Title:     "有意义中国经济指数",
		AppTitle:  new("月度总览"),
		Headings: []domain.Heading{
			{Level: 1, Text: "月度总览"},
		},
		Navigation: []domain.NavigationItem{},
		Metrics: []domain.Metric{
			{Label: "GDP 综合指数", Value: val},
		},
		Tables: []domain.DataTable{},
		Cards: []domain.IndexCard{
			{
				Code:        "LEI-GDP",
				Label:       "五大新产业规模占 GDP",
				Value:       val,
				Detail:      new("2026-06 · 同比"),
				Period:      new("2026-06"),
				Description: "指标解释",
			},
		},
		Charts: []domain.ChartData{
			{
				Title:       "产业规模图",
				Description: "产业规模图表说明",
				Notes:       []string{"数据截至 2026-06"},
				Series: []domain.ChartSeries{
					{
						Name: new("产业规模"),
						Type: new("scatter"),
						Points: []domain.ChartPoint{
							{
								Category: new("2026-06"),
								Value:    new(10.54),
							},
						},
					},
				},
			},
		},
		Text: []string{"LEI-GDP", "UPDATED", val, "2026-06", "新能源汽车产量持续增长"},
	}

	return domain.CrawledPage{
		Snapshot:      snapshot,
		HtmlDocuments: []string{"<main>fixture</main>"},
	}
}
