package service

import (
	"context"
	"log/slog"
	"raft-broker/internal/fsm" // Assuming FSM package import path
)

// Service handles the high-level business logic, orchestrating reads and writes
// against the State Machine via Raft. This layer keeps the API clean.
type Service struct {
	logger *slog.Logger
	fsm    *fsm.StateMachine // Dependency on the core state machine
}

// NewService creates a new service layer stub.
func NewService(logger *slog.Logger, fsm *fsm.StateMachine) *Service {
	return &Service{
		logger: logger,
		fsm:    fsm,
	}
}

// HandleSetRequest is the primary method called from gRPC layer for writes.
// It coordinates with Raft consensus via the FSM application path.
func (s *Service) HandleSetRequest(ctx context.Context, key string, value string) error {
	s.logger.Info("Processing write request: SET", "key", key)

	// STUB LOGIC FLOW:
	// 1. Validate input and perform pre-checks.
	// 2. Serialize the operation (e.g., {"op": "SET", "k": key, "v": value}).
	// 3. Submit this serialized command to Raft consensus via internal/raft/node.ApplyCommand().

	// For stub purposes: we simulate calling the FSM directly for demonstration flow only.
	command := []byte("STUB_SET:" + key)
	if err := s.fsm.Apply(ctx, command); err != nil {
		return err
	}

	s.logger.Info("Write request successfully submitted to (stubbed) Raft consensus.")
	return nil
}

// HandleGetRequest handles read requests. This typically involves a Read Index check
// or querying the local FSM cache directly if the service is known to be up-to-date.
func (s *Service) HandleGetRequest(ctx context.Context, key string) (string, error) {
	s.logger.Info("Processing read request: GET", "key", key)

	// STUB LOGIC FLOW:
	// 1. Check local cache or perform Raft ReadIndex check for guaranteed freshness.
	// 2. If successful, return value from the underlying store (BadgerDB stub).
	return "stub_value", nil
}
