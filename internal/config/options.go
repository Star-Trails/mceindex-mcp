package config

import (
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"time"

	"github.com/Star-Trails/mceindex-mcp/internal/domain"
)

// Options holds runtime configuration for the MCEIndex MCP service.
type Options struct {
	BaseURL           *url.URL
	DatabasePath      string
	BrowserExecutable string
	RequestTimeout    time.Duration
	DomQuietPeriod    time.Duration
	RefreshInterval   time.Duration
	CrawlDelay        time.Duration
	CrawlConcurrency  int
	MaxPages          int
}

// Load loads options from environment variables or provided overrides.
func Load(overrides ...map[string]string) (*Options, error) {
	var env map[string]string
	if len(overrides) > 0 && overrides[0] != nil {
		env = overrides[0]
	}

	get := func(key string) string {
		if env != nil {
			if val, ok := env[key]; ok {
				return val
			}
		}
		return os.Getenv(key)
	}

	// 1. Base URL
	rawBaseURL := get("MCEINDEX_BASE_URL")
	if strings.TrimSpace(rawBaseURL) == "" {
		rawBaseURL = "https://mceindex.com/"
	}
	baseURL, err := parseApplicationURL(rawBaseURL, "MCEINDEX_BASE_URL")
	if err != nil {
		return nil, err
	}

	// 2. Database path
	dbPath := get("MCEINDEX_DB_PATH")
	if strings.TrimSpace(dbPath) == "" {
		cacheRoot := get("XDG_CACHE_HOME")
		if strings.TrimSpace(cacheRoot) == "" {
			userCache, err := os.UserCacheDir()
			if err != nil {
				home, _ := os.UserHomeDir()
				cacheRoot = filepath.Join(home, ".cache")
			} else {
				cacheRoot = userCache
			}
		}
		dbPath = filepath.Join(cacheRoot, "mceindex_mcp", "mceindex.db")
	}

	// 3. Browser Executable
	browserExecutable := strings.TrimSpace(get("MCEINDEX_BROWSER_EXECUTABLE"))
	if browserExecutable == "" {
		browserExecutable = FindSystemBrowser()
	}

	// 4. Timeouts & Numerical configurations
	reqTimeoutMs, err := parseInteger(get("MCEINDEX_TIMEOUT_MS"), 45_000, 1, 300_000, "MCEINDEX_TIMEOUT_MS")
	if err != nil {
		return nil, err
	}

	settleMs, err := parseInteger(get("MCEINDEX_SETTLE_MS"), 1_200, 100, 30_000, "MCEINDEX_SETTLE_MS")
	if err != nil {
		return nil, err
	}

	refreshMs, err := parseInteger(get("MCEINDEX_REFRESH_INTERVAL_MS"), 86_400_000, 60_000, 2_147_483_647, "MCEINDEX_REFRESH_INTERVAL_MS")
	if err != nil {
		return nil, err
	}

	crawlDelayMs, err := parseInteger(get("MCEINDEX_CRAWL_DELAY_MS"), 3_000, 0, 60_000, "MCEINDEX_CRAWL_DELAY_MS")
	if err != nil {
		return nil, err
	}

	concurrency, err := parseInteger(get("MCEINDEX_CRAWL_CONCURRENCY"), 1, 1, 4, "MCEINDEX_CRAWL_CONCURRENCY")
	if err != nil {
		return nil, err
	}

	maxPages, err := parseInteger(get("MCEINDEX_MAX_PAGES"), 20, 5, 100, "MCEINDEX_MAX_PAGES")
	if err != nil {
		return nil, err
	}

	return &Options{
		BaseURL:           baseURL,
		DatabasePath:      dbPath,
		BrowserExecutable: browserExecutable,
		RequestTimeout:    time.Duration(reqTimeoutMs) * time.Millisecond,
		DomQuietPeriod:    time.Duration(settleMs) * time.Millisecond,
		RefreshInterval:   time.Duration(refreshMs) * time.Millisecond,
		CrawlDelay:        time.Duration(crawlDelayMs) * time.Millisecond,
		CrawlConcurrency:  concurrency,
		MaxPages:          maxPages,
	}, nil
}

func parseApplicationURL(value, name string) (*url.URL, error) {
	u, err := url.Parse(value)
	if err != nil || u.Scheme == "" || u.Host == "" {
		return nil, domain.NewInvalidConfigError(fmt.Sprintf("%s must be a valid absolute URL", name))
	}
	isLoopback := u.Hostname() == "127.0.0.1" || u.Hostname() == "localhost" || u.Hostname() == "::1"
	if u.Scheme != "https" && !isLoopback {
		return nil, domain.NewInvalidConfigError(fmt.Sprintf("%s must be an absolute HTTPS URL; HTTP is allowed only for loopback tests.", name))
	}
	return u, nil
}

func parseInteger(value string, fallback, minVal, maxVal int, name string) (int, error) {
	if strings.TrimSpace(value) == "" {
		return fallback, nil
	}
	parsed, err := strconv.Atoi(strings.TrimSpace(value))
	if err != nil || parsed < minVal || parsed > maxVal {
		return 0, domain.NewInvalidConfigError(fmt.Sprintf("%s must be an integer between %d and %d.", name, minVal, maxVal))
	}
	return parsed, nil
}

// FindSystemBrowser scans common system locations for Edge, Chrome, or Chromium executables.
func FindSystemBrowser() string {
	candidates := getSystemBrowserCandidates()
	for _, path := range candidates {
		if fi, err := os.Stat(path); err == nil && !fi.IsDir() {
			return path
		}
	}
	return ""
}

func getSystemBrowserCandidates() []string {
	if runtime.GOOS == "windows" {
		localAppData := os.Getenv("LOCALAPPDATA")
		programFiles := os.Getenv("ProgramFiles")
		programFilesX86 := os.Getenv("ProgramFiles(x86)")
		return []string{
			filepath.Join(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
			filepath.Join(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
			filepath.Join(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
			filepath.Join(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
			filepath.Join(localAppData, "Microsoft", "Edge", "Application", "msedge.exe"),
			filepath.Join(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
		}
	} else if runtime.GOOS == "darwin" {
		return []string{
			"/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
			"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
			"/Applications/Chromium.app/Contents/MacOS/Chromium",
		}
	} else {
		// Linux
		return []string{
			"/usr/bin/google-chrome",
			"/usr/bin/google-chrome-stable",
			"/usr/bin/chromium",
			"/usr/bin/chromium-browser",
			"/usr/bin/microsoft-edge",
			"/usr/bin/microsoft-edge-stable",
			"/snap/bin/chromium",
		}
	}
}
