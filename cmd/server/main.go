package main

import (
	"context"
	"fmt"
	"log/slog"
	"net"
	"os"
	"strconv"

	"raft-broker/internal/raft"
	"raft-broker/pkg/metrics"
)

// Config holds runtime configuration derived from environment variables.
type Config struct {
	NodeID string
	Port   string
}

// LoadConfig reads and validates environment variables.
func LoadConfig() (*Config, error) {
	nodeID := os.Getenv("NODE_ID")
	if nodeID == "" {
		return nil, fmt.Errorf("NODE_ID environment variable must be set")
	}

	port := os.Getenv("PORT")
	if port == "" {
		port = "50051"
	}

	// Validate port is numeric
	if _, err := strconv.Atoi(port); err != nil {
		return nil, fmt.Errorf("PORT must be a valid integer, got: %q", port)
	}

	return &Config{
		NodeID: nodeID,
		Port:   port,
	}, nil
}

// RunServer initializes and starts the Raft Broker service.
// Returns an error instead of calling os.Exit to enable testing.
func RunServer(ctx context.Context) error {
	logger := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{
		Level: slog.LevelInfo,
	}))

	cfg, err := LoadConfig()
	if err != nil {
		return fmt.Errorf("configuration error: %w", err)
	}

	logger.Info("Starting Raft Broker service",
		"node_id", cfg.NodeID,
		"port", cfg.Port,
	)

	// Initialize metric registry
	metrics.RegisterMetrics()

	// Initialize Raft node
	node, err := raft.NewRaftNode(ctx, cfg.NodeID, logger)
	if err != nil {
		return fmt.Errorf("failed to initialize Raft node: %w", err)
	}
	defer func() {
		if r := raft.ShutdownNode(node); r != nil {
			logger.Error("Error during Raft node shutdown", "error", r)
		}
	}()

	// Start gRPC server
	listenAddr := fmt.Sprintf(":%s", cfg.Port)
	lis, err := net.Listen("tcp", listenAddr)
	if err != nil {
		return fmt.Errorf("failed to listen on %s: %w", listenAddr, err)
	}
	defer lis.Close()

	logger.Info("gRPC server listening", "address", lis.Addr().String())

	// Stub: In a real scenario, we would register the generated pb.ServiceServer here.
	// The actual gRPC server would be started with grpc.NewServer() and serve(lis).

	// Block until context is cancelled
	<-ctx.Done()
	logger.Info("Shutting down server...")
	return nil
}

func main() {
	if err := RunServer(context.Background()); err != nil {
		slog.Error("Server failed", "error", err)
		os.Exit(1)
	}
}