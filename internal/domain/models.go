package domain

import "time"

// NavigationKind denotes the type of navigation element.
type NavigationKind string

const (
	NavKindLink   NavigationKind = "link"
	NavKindButton NavigationKind = "button"
	NavKindTab    NavigationKind = "tab"
)

// ContentKind denotes the content element type in a page.
type ContentKind string

const (
	ContentHeading ContentKind = "heading"
	ContentMetric  ContentKind = "metric"
	ContentText    ContentKind = "text"
	ContentTable   ContentKind = "table"
	ContentChart   ContentKind = "chart"
)

// SearchMode defines the search matching mode.
type SearchMode string

const (
	SearchModeAnd    SearchMode = "and"
	SearchModePhrase SearchMode = "phrase"
)

// PageView specifies what part of a page to inspect.
type PageView string

const (
	PageViewSummary PageView = "summary"
	PageViewContent PageView = "content"
	PageViewTables  PageView = "tables"
	PageViewCharts  PageView = "charts"
)

// RefreshOutcome denotes the result of an index refresh operation.
type RefreshOutcome string

const (
	RefreshCompleted RefreshOutcome = "completed"
	RefreshPartial   RefreshOutcome = "partial"
	RefreshSkipped   RefreshOutcome = "skipped"
)

type Heading struct {
	Level int    `json:"level"`
	Text  string `json:"text"`
}

type NavigationItem struct {
	Text string         `json:"text"`
	Kind NavigationKind `json:"kind"`
	URL  *string        `json:"url,omitempty"`
}

type Metric struct {
	Label string  `json:"label"`
	Value string  `json:"value"`
	Delta *string `json:"delta,omitempty"`
	Help  *string `json:"help,omitempty"`
}

type DataTable struct {
	Headers []string   `json:"headers"`
	Rows    [][]string `json:"rows"`
	Title   *string    `json:"title,omitempty"`
}

type ChartPoint struct {
	Category     *string  `json:"category,omitempty"`
	Value        *float64 `json:"value,omitempty"`
	Text         *string  `json:"text,omitempty"`
	DisplayValue *string  `json:"displayValue,omitempty"`
}

type ChartSeries struct {
	Name   *string      `json:"name,omitempty"`
	Type   *string      `json:"type,omitempty"`
	Points []ChartPoint `json:"points"`
}

type ChartData struct {
	Title       string        `json:"title"`
	Description string        `json:"description"`
	Notes       []string      `json:"notes"`
	XAxisTitle  *string       `json:"xAxisTitle,omitempty"`
	YAxisTitle  *string       `json:"yAxisTitle,omitempty"`
	Series      []ChartSeries `json:"series"`
}

type IndexCard struct {
	Code        string  `json:"code"`
	Label       string  `json:"label"`
	Value       string  `json:"value"`
	Detail      *string `json:"detail,omitempty"`
	Period      *string `json:"period,omitempty"`
	Description string  `json:"description"`
}

type PageSnapshot struct {
	SourceURL   string           `json:"sourceUrl"`
	FetchedAt   time.Time        `json:"fetchedAt"`
	Title       string           `json:"title"`
	Description *string          `json:"description,omitempty"`
	AppTitle    *string          `json:"appTitle,omitempty"`
	Headings    []Heading        `json:"headings"`
	Navigation  []NavigationItem `json:"navigation"`
	Metrics     []Metric         `json:"metrics"`
	Tables      []DataTable      `json:"tables"`
	Cards       []IndexCard      `json:"cards"`
	Charts      []ChartData      `json:"charts"`
	Text        []string         `json:"text"`
}

type CrawledPage struct {
	Snapshot      PageSnapshot `json:"snapshot"`
	HtmlDocuments []string     `json:"htmlDocuments"`
}

type IndexedPage struct {
	Slug    string      `json:"slug"`
	Label   string      `json:"label"`
	Crawled CrawledPage `json:"crawled"`
}

type StoredPageSummary struct {
	Slug          string    `json:"slug"`
	Label         string    `json:"label"`
	Title         string    `json:"title"`
	SourceURL     string    `json:"sourceUrl"`
	FetchedAt     time.Time `json:"fetchedAt"`
	LastCheckedAt time.Time `json:"lastCheckedAt"`
	TextCount     int       `json:"textCount"`
	Generation    int64     `json:"generation"`
}

type StoredPage struct {
	Summary  StoredPageSummary `json:"summary"`
	Snapshot PageSnapshot      `json:"snapshot"`
}

type PageContentItem struct {
	ID       int64       `json:"id"`
	Kind     ContentKind `json:"kind"`
	Text     string      `json:"text"`
	Sequence int         `json:"sequence"`
}

type SearchHit struct {
	EntryID   int64       `json:"entryId"`
	PageSlug  string      `json:"pageSlug"`
	PageLabel string      `json:"pageLabel"`
	SourceURL string      `json:"sourceUrl"`
	FetchedAt time.Time   `json:"fetchedAt"`
	Kind      ContentKind `json:"kind"`
	Text      string      `json:"text"`
	Rank      float64     `json:"rank"`
}

type CrawlFailure struct {
	URL     string `json:"url"`
	Code    string `json:"code"`
	Message string `json:"message"`
}

type CrawlReport struct {
	StartedAt      time.Time      `json:"startedAt"`
	FinishedAt     time.Time      `json:"finishedAt"`
	Outcome        RefreshOutcome `json:"outcome"`
	PagesChecked   int            `json:"pagesChecked"`
	ChangedPages   int            `json:"changedPages"`
	UnchangedPages int            `json:"unchangedPages"`
	Failures       []CrawlFailure `json:"failures"`
}

type IndexStatus struct {
	DatabasePath          string     `json:"databasePath"`
	SchemaVersion         int        `json:"schemaVersion"`
	PageCount             int        `json:"pageCount"`
	Generation            int64      `json:"generation"`
	LastSuccessfulRefresh *time.Time `json:"lastSuccessfulRefresh,omitempty"`
	LastAttempt           *time.Time `json:"lastAttempt,omitempty"`
	Stale                 bool       `json:"stale"`
	Refreshing            bool       `json:"refreshing"`
	LastError             *string    `json:"lastError,omitempty"`
}

type OverviewNoteKind string

const (
	NoteFormula     OverviewNoteKind = "formula"
	NoteDataSource  OverviewNoteKind = "dataSource"
	NoteMethodology OverviewNoteKind = "methodology"
	NoteCaveat      OverviewNoteKind = "caveat"
)

type OverviewNote struct {
	Kind       OverviewNoteKind `json:"kind"`
	Text       string           `json:"text"`
	SourcePage string           `json:"sourcePage"`
	SourceURL  string           `json:"sourceUrl"`
}

type ConclusionStatus string

const (
	ConclusionVerified           ConclusionStatus = "verified"
	ConclusionPartiallyVerified  ConclusionStatus = "partiallyVerified"
	ConclusionNotFound           ConclusionStatus = "notFound"
	ConclusionUnverifiedEstimate ConclusionStatus = "unverifiedEstimate"
	ConclusionNotAssessed        ConclusionStatus = "notAssessed"
)

type EvidenceStatus string

const (
	EvidenceVerified    EvidenceStatus = "verified"
	EvidencePartial     EvidenceStatus = "partial"
	EvidenceMissing     EvidenceStatus = "missing"
	EvidenceNotAssessed EvidenceStatus = "notAssessed"
)

type AlgorithmStatus string

const (
	AlgorithmPublished     AlgorithmStatus = "published"
	AlgorithmInferred      AlgorithmStatus = "inferred"
	AlgorithmMissing       AlgorithmStatus = "missing"
	AlgorithmNotApplicable AlgorithmStatus = "notApplicable"
	AlgorithmNotAssessed   AlgorithmStatus = "notAssessed"
)

type ReproductionStatus string

const (
	ReproductionVerified     ReproductionStatus = "verified"
	ReproductionConditional  ReproductionStatus = "conditional"
	ReproductionImpossible   ReproductionStatus = "impossible"
	ReproductionDirectSource ReproductionStatus = "directSource"
	ReproductionNotAssessed  ReproductionStatus = "notAssessed"
)

type EvidenceSource struct {
	Publisher string  `json:"publisher"`
	Title     string  `json:"title"`
	URL       string  `json:"url"`
	Period    *string `json:"period,omitempty"`
	Detail    *string `json:"detail,omitempty"`
}

type ConceptualProvenanceStatus string

const (
	ProvenanceVerified          ConceptualProvenanceStatus = "verified"
	ProvenancePartiallyVerified ConceptualProvenanceStatus = "partiallyVerified"
	ProvenanceNotFound          ConceptualProvenanceStatus = "notFound"
	ProvenanceNotAssessed       ConceptualProvenanceStatus = "notAssessed"
)

type ConceptualProvenance struct {
	Status      ConceptualProvenanceStatus `json:"status"`
	Summary     string                     `json:"summary"`
	Sources     []EvidenceSource           `json:"sources"`
	Limitations []string                   `json:"limitations"`
}

type ConclusionVerification struct {
	AuditedPeriod          string                `json:"auditedPeriod"`
	AppliesToCurrentPeriod bool                  `json:"appliesToCurrentPeriod"`
	DataUpdated            bool                  `json:"dataUpdated"`
	Status                 ConclusionStatus      `json:"status"`
	SourceStatus           EvidenceStatus        `json:"sourceStatus"`
	AlgorithmStatus        AlgorithmStatus       `json:"algorithmStatus"`
	ReproductionStatus     ReproductionStatus    `json:"reproductionStatus"`
	IndependentExactMatch  bool                  `json:"independentExactMatch"`
	Summary                string                `json:"summary"`
	Formula                *string               `json:"formula,omitempty"`
	Reproduction           *string               `json:"reproduction,omitempty"`
	Sources                []EvidenceSource      `json:"sources"`
	Limitations            []string              `json:"limitations"`
	ConceptualProvenance   *ConceptualProvenance `json:"conceptualProvenance,omitempty"`
}

type TrendDirection string

const (
	TrendRising           TrendDirection = "rising"
	TrendFalling          TrendDirection = "falling"
	TrendStable           TrendDirection = "stable"
	TrendMixed            TrendDirection = "mixed"
	TrendInsufficientData TrendDirection = "insufficientData"
)

type EconomicAssessment string

const (
	AssessmentImproving        EconomicAssessment = "improving"
	AssessmentDeteriorating    EconomicAssessment = "deteriorating"
	AssessmentStable           EconomicAssessment = "stable"
	AssessmentMixed            EconomicAssessment = "mixed"
	AssessmentIndeterminate    EconomicAssessment = "indeterminate"
	AssessmentInsufficientData EconomicAssessment = "insufficientData"
)

type HistoricalObservation struct {
	Period string  `json:"period"`
	Value  float64 `json:"value"`
}

type IndicatorTrend struct {
	SeriesKey                  string                  `json:"seriesKey"`
	Label                      string                  `json:"label"`
	Unit                       string                  `json:"unit"`
	AvailablePeriods           int                     `json:"availablePeriods"`
	History                    []HistoricalObservation `json:"history"`
	CurrentPeriod              string                  `json:"currentPeriod"`
	Current                    float64                 `json:"current"`
	PreviousPeriod             *string                 `json:"previousPeriod,omitempty"`
	Previous                   *float64                `json:"previous,omitempty"`
	MonthOverMonthChange       *float64                `json:"monthOverMonthChange,omitempty"`
	YearAgoPeriod              *string                 `json:"yearAgoPeriod,omitempty"`
	YearAgo                    *float64                `json:"yearAgo,omitempty"`
	YearOverYearChange         *float64                `json:"yearOverYearChange,omitempty"`
	RecentThreeMonthAverage    *float64                `json:"recentThreeMonthAverage,omitempty"`
	PreviousThreeMonthAverage  *float64                `json:"previousThreeMonthAverage,omitempty"`
	ThreeMonthMomentum         *float64                `json:"threeMonthMomentum,omitempty"`
	Direction                  TrendDirection          `json:"direction"`
	Assessment                 EconomicAssessment      `json:"assessment"`
	Basis                      string                  `json:"basis"`
	Interpretation             string                  `json:"interpretation"`
}

type OverviewReading struct {
	Key          string                  `json:"key"`
	Label        string                  `json:"label"`
	Value        *float64                `json:"value,omitempty"`
	DisplayValue string                  `json:"displayValue"`
	Unit         *string                 `json:"unit,omitempty"`
	Verification *ConclusionVerification `json:"verification,omitempty"`
}

type OverviewSection struct {
	Code        string            `json:"code"`
	Title       string            `json:"title"`
	Period      *string           `json:"period,omitempty"`
	Description string            `json:"description"`
	Readings    []OverviewReading `json:"readings"`
	Notes       []OverviewNote    `json:"notes"`
	Trend       *IndicatorTrend   `json:"trend,omitempty"`
}

type LatestOverview struct {
	SourceURL  string            `json:"sourceUrl"`
	FetchedAt  time.Time         `json:"fetchedAt"`
	Generation int64             `json:"generation"`
	AtAGlance  []OverviewSection `json:"atAGlance"`
	Cards      []IndexCard       `json:"cards"`
	Headings   []string          `json:"headings"`
	Notes      []string          `json:"notes"`
}

type PageListResult struct {
	Status IndexStatus         `json:"status"`
	Pages  []StoredPageSummary `json:"pages"`
}

type PageResult struct {
	Page       StoredPageSummary `json:"page"`
	View       PageView          `json:"view"`
	Cards      []IndexCard       `json:"cards"`
	Headings   []Heading         `json:"headings"`
	Metrics    []Metric          `json:"metrics"`
	Items      []PageContentItem `json:"items"`
	Tables     []DataTable       `json:"tables"`
	Charts     []ChartData       `json:"charts"`
	Offset     int               `json:"offset"`
	NextOffset *int              `json:"nextOffset,omitempty"`
	HasMore    bool              `json:"hasMore"`
}

type SearchResult struct {
	Query      string        `json:"query"`
	Page       *string       `json:"page,omitempty"`
	Kind       *ContentKind  `json:"kind,omitempty"`
	Hits       []SearchHit   `json:"hits"`
	Offset     int           `json:"offset"`
	NextOffset *int          `json:"nextOffset,omitempty"`
	HasMore    bool          `json:"hasMore"`
	Generation int64         `json:"generation"`
}

type DiscoveryReading struct {
	Key          string  `json:"key"`
	Label        string  `json:"label"`
	DisplayValue string  `json:"displayValue"`
	Unit         *string `json:"unit,omitempty"`
}

type DiscoveryTopic struct {
	Code              string             `json:"code"`
	Title             string             `json:"title"`
	Period            *string            `json:"period,omitempty"`
	Meaning           string             `json:"meaning"`
	WhyItMatters      string             `json:"whyItMatters"`
	SuggestedQuestion string             `json:"suggestedQuestion"`
	CurrentReadings   []DiscoveryReading `json:"currentReadings"`
	DetailTool        string             `json:"detailTool"`
	DetailArgument    string             `json:"detailArgument"`
	Trend             *IndicatorTrend    `json:"trend,omitempty"`
}

type ToolRecommendation struct {
	Need    string  `json:"need"`
	Tool    string  `json:"tool"`
	Example *string `json:"example,omitempty"`
}

type DataDiscoveryResult struct {
	Summary            string               `json:"summary"`
	SourceURL          string               `json:"sourceUrl"`
	FetchedAt          time.Time            `json:"fetchedAt"`
	Generation         int64                `json:"generation"`
	Topics             []DiscoveryTopic     `json:"topics"`
	Pages              []StoredPageSummary  `json:"pages"`
	SuggestedQuestions []string             `json:"suggestedQuestions"`
	NextSteps          []ToolRecommendation `json:"nextSteps"`
}

type RefreshResult struct {
	Report CrawlReport `json:"report"`
	Status IndexStatus `json:"status"`
}

type IndicatorResult struct {
	Indicator  IndexCard       `json:"indicator"`
	SourceURL  string          `json:"sourceUrl"`
	FetchedAt  time.Time       `json:"fetchedAt"`
	Generation int64           `json:"generation"`
	Trend      *IndicatorTrend `json:"trend,omitempty"`
}
