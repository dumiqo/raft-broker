package main

import (
	"os"
	"testing"
)

// TestMain sets up and tears down necessary environment variables or mocks
// before running tests in the package.
func TestMain(m *testing.M) {
	// Save original state to restore later
	originalEnv := map[string]string{
		"NODE_ID": os.Getenv("NODE_ID"),
		"PORT":    os.Getenv("PORT"),
	}

	code := m.Run()

	// Restore environment variables after all tests run
	for k, v := range originalEnv {
		if v == "" && os.Getenv(k) != "" {
			os.Unsetenv(k)
		} else {
			os.Setenv(k, v)
		}
	}

	os.Exit(code)
}

func TestLoadConfig_MissingNodeID(t *testing.T) {
	os.Unsetenv("NODE_ID")
	os.Unsetenv("PORT")

	cfg, err := LoadConfig()
	if err == nil {
		t.Fatal("Expected error when NODE_ID is missing, got nil")
	}
	if cfg != nil {
		t.Fatalf("Expected nil config, got %v", cfg)
	}
}

func TestLoadConfig_DefaultPort(t *testing.T) {
	os.Setenv("NODE_ID", "test-node")
	defer os.Unsetenv("NODE_ID")
	os.Unsetenv("PORT")

	cfg, err := LoadConfig()
	if err != nil {
		t.Fatalf("Unexpected error: %v", err)
	}
	if cfg.NodeID != "test-node" {
		t.Errorf("Expected NodeID 'test-node', got %q", cfg.NodeID)
	}
	if cfg.Port != "50051" {
		t.Errorf("Expected default Port '50051', got %q", cfg.Port)
	}
}

func TestLoadConfig_CustomPort(t *testing.T) {
	os.Setenv("NODE_ID", "test-node")
	os.Setenv("PORT", "9090")
	defer os.Unsetenv("NODE_ID")
	defer os.Unsetenv("PORT")

	cfg, err := LoadConfig()
	if err != nil {
		t.Fatalf("Unexpected error: %v", err)
	}
	if cfg.Port != "9090" {
		t.Errorf("Expected Port '9090', got %q", cfg.Port)
	}
}

func TestLoadConfig_InvalidPort(t *testing.T) {
	os.Setenv("NODE_ID", "test-node")
	os.Setenv("PORT", "not-a-port")
	defer os.Unsetenv("NODE_ID")
	defer os.Unsetenv("PORT")

	_, err := LoadConfig()
	if err == nil {
		t.Fatal("Expected error for invalid PORT, got nil")
	}
}

func TestLoadConfig_EnvCleanup(t *testing.T) {
	// Verify that TestMain properly restores environment
	os.Unsetenv("NODE_ID")
	os.Unsetenv("PORT")

	// Set a value, then unset to verify cleanup
	os.Setenv("NODE_ID", "temp-node")
	os.Unsetenv("NODE_ID")

	cfg, err := LoadConfig()
	if err == nil {
		t.Fatal("Expected error after cleanup, got nil")
	}
	if cfg != nil {
		t.Fatalf("Expected nil config, got %v", cfg)
	}
}