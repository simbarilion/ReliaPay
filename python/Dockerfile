# syntax=docker/dockerfile:1

FROM python:3.12-slim-bookworm

WORKDIR /app
ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1

COPY python/perimeter /app/perimeter

USER nobody
CMD ["python", "-m", "perimeter"]
