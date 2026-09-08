package raft

import (
	"context"
	"fmt"
	"log/slog"
	"sync"
)

// RaftNode encapsulates the entire Raft cluster membership and state machine interaction.
type RaftNode struct {
	nodeID  string
	logger  *slog.Logger
	mu      sync.RWMutex
	leader  bool
	closed  bool
}

// NewRaftNode initializes a new Raft node instance.
func NewRaftNode(ctx context.Context, id string, logger *slog.Logger) (*RaftNode, error) {
	logger.Info("Initializing Raft node", "node_id", id)

	return &RaftNode{
		nodeID: id,
		logger: logger,
		leader: false,
	}, nil
}

// IsLeader returns the current leadership status of the node.
// Thread-safe via mutex.
func (r *RaftNode) IsLeader() bool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return r.leader
}

// SetLeader sets the leadership status. Used internally during leader election.
func (r *RaftNode) SetLeader(isLeader bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.leader = isLeader
}

// ApplyCommand submits a command to the Raft log for consensus.
// In a real implementation, this would use hashicorp/raft's Apply method.
func (r *RaftNode) ApplyCommand(ctx context.Context, command interface{}) error {
	r.mu.RLock()
	if r.closed {
		r.mu.RUnlock()
		return ErrNodeClosed
	}
	r.mu.RUnlock()

	r.logger.Debug("ApplyCommand called", "command", command)
	// Stub: In a real implementation, this would submit the command to the FSM for guaranteed sequential processing.
	return nil
}

// ShutdownNode gracefully shuts down the Raft node.
func ShutdownNode(r *RaftNode) error {
	if r == nil {
		return nil
	}

	r.mu.Lock()
	defer r.mu.Unlock()

	if r.closed {
		return nil
	}

	r.logger.Info("Shutting down Raft node", "node_id", r.nodeID)
	r.closed = true
	r.leader = false
	return nil
}

// ErrNodeClosed is returned when operating on a shut-down node.
var ErrNodeClosed = fmt.Errorf("raft node is closed")