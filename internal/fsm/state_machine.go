package fsm

import (
	"context"
	"fmt"
	"log/slog"
)

// StateMachine implements the core application state logic that Raft commits to.
type StateMachine struct {
	logger *slog.Logger
	// Placeholder for the persistent store interface, e.g., badger.DB or bolt.DB connection pool
}

// NewStateMachine creates a new FSM instance stub.
func NewStateMachine(logger *slog.Logger) *StateMachine {
	return &StateMachine{
		logger: logger,
	}
}

// Apply processes an entry committed by the Raft cluster. This MUST be idempotent and linearizable.
// The input 'command' is expected to be a structured command (e.g., JSON byte array).
func (s *StateMachine) Apply(ctx context.Context, command []byte) error {
	s.logger.Info("Stub: FSM Apply called. Processing committed log entry.")

	// CRITICAL LOGIC STUB:
	// 1. Deserialize the command byte slice into a structured operation type.
	// 2. Execute the state change (SET, DELETE, etc.) against the persistent store (BadgerDB).

	// Example stub behavior:
	if len(command) < 5 {
		return fmt.Errorf("invalid or empty command payload")
	}

	s.logger.Debug("Successfully processed FSM state change for a key.")
	return nil
}
