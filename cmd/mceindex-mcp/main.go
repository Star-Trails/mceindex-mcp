package main

import (
	"fmt"
	"log"
	"os"

	"github.com/Star-Trails/mceindex-mcp/internal/config"
	"github.com/Star-Trails/mceindex-mcp/internal/crawling"
	"github.com/Star-Trails/mceindex-mcp/internal/parsing"
	"github.com/Star-Trails/mceindex-mcp/internal/services"
	"github.com/Star-Trails/mceindex-mcp/internal/store"
	"github.com/Star-Trails/mceindex-mcp/internal/tools"
	"github.com/mark3labs/mcp-go/server"
)

const version = "4.0.1"

func main() {
	// Send logs strictly to stderr so stdout remains clean for MCP JSON-RPC protocol
	log.SetOutput(os.Stderr)
	log.SetFlags(log.LstdFlags | log.Lmicroseconds)

	cfg, err := config.Load()
	if err != nil {
		fmt.Fprintf(os.Stderr, "Fatal configuration error: %v\n", err)
		os.Exit(1)
	}

	parser := parsing.NewParser()
	browserRunner := crawling.NewBrowserRunner(cfg)
	defer browserRunner.Close()

	crawler := crawling.NewCrawler(cfg, parser, browserRunner)
	defer crawler.Close()

	st, err := store.NewStore(cfg.DatabasePath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Fatal storage error: %v\n", err)
		os.Exit(1)
	}
	defer st.Close()

	coordinator := services.NewRefreshCoordinator(cfg, st, crawler)
	defer coordinator.Close()

	mceService := services.NewMceIndexService(st, coordinator)

	mcpServer := server.NewMCPServer(
		"mceindex-mcp",
		version,
	)

	tools.RegisterTools(mcpServer, mceService)

	log.Printf("Starting MCEIndex MCP Server v%s (db: %s)...", version, cfg.DatabasePath)

	if err := server.ServeStdio(mcpServer); err != nil {
		fmt.Fprintf(os.Stderr, "Server error: %v\n", err)
		os.Exit(1)
	}
}
