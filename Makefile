# Makefile for Raft Broker Service

.PHONY: all setup build-server test lint clean

all: setup

setup: go mod tidy
	@echo "--- Setup complete. Run 'make build-server' to compile and run."

build-server:
	go build -o bin/raft-broker ./cmd/server/main.go

test:
	# Placeholder for running all unit and integration tests
	go test ./...

lint:
	golangci-lint run --config .golangci.yml


generate-api:
	@echo "--- API Generation Target ---"
	@echo "NOTE: This target must be implemented with your actual protoc compilation steps."
	# Example: protoc --go_out=. --go_opt=paths=source_relative internal/api/proto/*.proto