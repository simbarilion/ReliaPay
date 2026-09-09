# syntax=docker/dockerfile:1

FROM python:3.12-slim-bookworm

WORKDIR /app
ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1

COPY python/requirements.txt /app/requirements.txt
RUN pip install --no-cache-dir -r /app/requirements.txt

COPY python/perimeter /app/perimeter

USER nobody
CMD ["python", "-m", "perimeter"]
