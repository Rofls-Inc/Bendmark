# Контекст сборки - корень репозитория (см. docker-compose.yml): код gRPC
# генерируется из proto/, который лежит вне services/analyzer

# Генерация кода из proto: grpcio-tools нужен только здесь
FROM python:3.12-slim AS proto
RUN pip install --no-cache-dir grpcio-tools==1.70.0
WORKDIR /src
COPY proto/ proto/
COPY services/analyzer/gen_proto.py services/analyzer/
RUN python services/analyzer/gen_proto.py

FROM python:3.12-slim
WORKDIR /app
COPY services/analyzer/pyproject.toml .
COPY services/analyzer/analyzer/ analyzer/
# [server] - grpcio и protobuf для gRPC-сервиса; CLI работает и без них
RUN pip install --no-cache-dir ".[server]"
COPY --from=proto /src/services/analyzer/generated/ generated/
USER nobody
EXPOSE 50052
# По умолчанию gRPC-сервис; CLI: docker compose run --rm analyzer-cli <result.json>
ENTRYPOINT ["python", "-m", "analyzer.server"]
CMD ["--listen", "0.0.0.0:50052"]
