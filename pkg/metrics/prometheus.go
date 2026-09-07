package metrics

import (
	"github.com/prometheus/client_golang/prometheus"
	"github.com/prometheus/client_golang/prometheus/promauto"
)

// Global registry for all service metrics
var (
	// Counter for total requests received by the gRPC layer
	RequestCount = promauto.NewCounterVec(prometheus.CounterOpts{
		Name: "raft_broker_http_requests_total",
		Help: "Total number of RPC requests processed by endpoint and method.",
	}, []string{"endpoint", "method"})

	// Histogram to measure the latency of core business operations (e.g., FSM application)
	OperationLatency = promauto.NewHistogram(prometheus.HistogramOpts{
		Name:    "raft_broker_operation_duration_seconds",
		Help:    "Duration histogram for core service operations.",
		Buckets: prometheus.DefBuckets, // Standard Prometheus buckets (0.1s, 0.25s, etc.)
	})

	// Gauge to track the current Raft cluster status (Leader/Follower/Candidate)
	RaftStateGauge = promauto.NewGaugeVec(prometheus.GaugeOpts{
		Name: "raft_broker_node_state",
		Help: "The current state of the node: 1=Leader, 2=Follower, 3=Candidate.",
	}, []string{"node_id"})

)

// RegisterMetrics performs initial setup and validation for all metrics.
func RegisterMetrics() {
	// By using promauto, most registration happens automatically, but this function 
	// acts as the explicit hook point in main.go to confirm initialization.
	// Add any custom collectors or checks here if needed.

	// Example stub update: set a default state for initial check
	RaftStateGauge.WithLabelValues("stub-node").Set(2) // Start assuming Follower
}