package main

import (
	"context"
	"log/slog"
	"net"
	"os"
	"time"

	// Placeholder imports for dependencies we must stub out
	// Assuming this path is correct after proto compilation
	"raft-broker/internal/raft"
	"raft-broker/pkg/metrics"
)

func main() {
	// 1. Configuration Loading (Stub: Use environment variables)
	nodeID := os.Getenv("RAFT_NODE_ID")
	if nodeID == "" {
		slog.Error("FATAL: RAFT_NODE_ID environment variable must be set.")
		os.Exit(1)
	}

	// 2. Logger and Metric Initialization
	logger := slog.New(slog.NewTextHandler(os.Stdout, nil))
	metrics.RegisterMetrics() // Initialize the metric registry globally

	slog.Info("Starting Raft Broker service...")

	// 3. Initialize Raft Node (Stub)
	_, err := raft.NewRaftNode(context.Background(), nodeID, logger)
	if err != nil {
		logger.Error("Failed to initialize Raft Node", "error", err)
		os.Exit(1)
	}

	// 4. Start gRPC Server and Service Orchestration (Stub)
	grpcServer := func() {
		_, err := net.Listen("tcp", ":50051")
		if err != nil {
			logger.Error("Failed to listen on gRPC port", "error", err)
			return
		}

		// Stub: In a real scenario, we would register the generated pb.ServiceServer interface implementation here.
		slog.Info("Stubbing gRPC server start on :50051...")

		go func() {
			// Simulate waiting for connections/requests
			time.Sleep(2 * time.Second)
			logger.Info("gRPC stub listening successfully (no requests handled yet).")
		}()
	}

	grpcServer()

	// Keep the main routine alive until signaled to stop
	slog.Info("Service initialized and running. Press Ctrl+C to exit.")
	select {}
}
