package crawling

import (
	"context"
	"fmt"
	"os"
	"sync"

	"github.com/Star-Trails/mceindex-mcp/internal/config"
	"github.com/Star-Trails/mceindex-mcp/internal/domain"
	"github.com/go-rod/rod"
	"github.com/go-rod/rod/lib/launcher"
	"github.com/go-rod/stealth"
)

// BrowserRunner manages the headless browser lifecycle with stealth anti-detection capabilities.
type BrowserRunner struct {
	executablePath string
	browser        *rod.Browser
	mu             sync.Mutex
	disposed       bool
}

// NewBrowserRunner creates a new BrowserRunner with configured or auto-detected executable.
func NewBrowserRunner(cfg *config.Options) *BrowserRunner {
	execPath := cfg.BrowserExecutable
	if execPath == "" {
		execPath = config.FindSystemBrowser()
	}
	return &BrowserRunner{
		executablePath: execPath,
	}
}

// EnsureBrowser initializes the browser instance if not already running.
func (r *BrowserRunner) EnsureBrowser(ctx context.Context) (*rod.Browser, error) {
	r.mu.Lock()
	defer r.mu.Unlock()

	if r.disposed {
		return nil, domain.NewError(domain.ErrCodeBrowserNotFound, "BrowserRunner is closed.")
	}

	if r.browser != nil {
		return r.browser, nil
	}

	l := launcher.New().
		Headless(true).
		Set("disable-blink-features", "AutomationControlled").
		Set("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36")

	if r.executablePath != "" {
		if fi, err := os.Stat(r.executablePath); err == nil && !fi.IsDir() {
			l = l.Bin(r.executablePath)
		}
	}

	controlURL, err := l.Context(ctx).Launch()
	if err != nil {
		return nil, domain.WrapError(
			domain.ErrCodeBrowserNotFound,
			fmt.Sprintf("Failed to launch headless browser (executable: %s)", r.executablePath),
			err,
		)
	}

	browser := rod.New().ControlURL(controlURL)
	if err := browser.Connect(); err != nil {
		return nil, domain.WrapError(domain.ErrCodeBrowserNotFound, "Failed to connect to browser CDP", err)
	}

	r.browser = browser
	return r.browser, nil
}

// OpenStealthPage opens a new stealth tab.
func (r *BrowserRunner) OpenStealthPage(ctx context.Context) (*rod.Page, error) {
	b, err := r.EnsureBrowser(ctx)
	if err != nil {
		return nil, err
	}

	page, err := stealth.Page(b)
	if err != nil {
		return nil, domain.WrapError(domain.ErrCodeInternalError, "Failed to create stealth page", err)
	}

	return page.Context(ctx), nil
}

// Close gracefully closes the browser process.
func (r *BrowserRunner) Close() error {
	r.mu.Lock()
	defer r.mu.Unlock()

	r.disposed = true
	if r.browser != nil {
		err := r.browser.Close()
		r.browser = nil
		return err
	}
	return nil
}
