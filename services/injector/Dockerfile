FROM golang:1.27-alpine AS build
WORKDIR /src
COPY go.mod ./
COPY *.go ./
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/injector .

FROM scratch
COPY --from=build /out/injector /injector
USER 65532:65532
EXPOSE 8081
ENTRYPOINT ["/injector"]
