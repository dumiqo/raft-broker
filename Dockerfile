# Stage 1: Build the application
FROM golang:1.21-alpine AS builder

# Set necessary build flags and paths
WORKDIR /app

# Copy go module files
COPY go.mod .
COPY go.sum .

# Download dependencies first to cache layer
RUN go mod download

# Copy source code
COPY ./cmd/server .
COPY internal ./internal
COPY pkg ./pkg

# Build the binary statically for minimal base image compatibility
# Using CGO_ENABLED=0 ensures a static build, which is better for Alpine containers.
RUN CGO_ENABLED=0 go build -ldflags="-s -w" -o /broker ./cmd/server/main.go

# Stage 2: Create the final minimal runtime image
FROM alpine:latest

# Install necessary CA certificates if external calls are made (good practice)
RUN apk --no-cache add ca-certificates

WORKDIR /app

# Copy only the built binary from the builder stage
COPY --from=builder /broker /app/broker

# Define required environment variables and default command execution
ENV PORT=8080
ENV NODE_ID=default-node

# Expose the port dynamically (although we will map it in compose)
EXPOSE 8080

ENTRYPOINT ["/app/broker"]