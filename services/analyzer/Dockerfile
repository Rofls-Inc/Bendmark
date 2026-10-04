FROM python:3.12-slim
WORKDIR /app
COPY pyproject.toml .
COPY analyzer/ analyzer/
RUN pip install --no-cache-dir .
USER nobody
ENTRYPOINT ["python", "-m", "analyzer"]
