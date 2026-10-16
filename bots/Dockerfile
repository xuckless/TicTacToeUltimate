# Bot service (botd). Build context: repo root (needs spec/ only for tests, not at runtime).
FROM golang:1.24-alpine AS build
WORKDIR /src/bots
COPY bots/go.mod bots/go.sum ./
RUN go mod download
COPY bots/ ./
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/botd ./cmd/botd

FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /out/botd /botd
# Personas (bot API keys) come from a mounted secret: TTT_BOT_PERSONAS=/secrets/personas.json
ENV TTT_BOT_LISTEN=:8080
EXPOSE 8080
ENTRYPOINT ["/botd"]
