# Basis image met Python 3.11
FROM python:3.11-slim-bookworm

# OpenDataLoader PDF vereist Java 11+
# In Debian slim images moet de man folder bestaan voor het headless JRE package
RUN mkdir -p /usr/share/man/man1 /usr/share/man/man2 && \
    apt-get update && apt-get install -y --no-install-recommends \
    default-jre-headless \
    curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Installeer dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Kopieer applicatiecode
COPY main.py .

EXPOSE 8080

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8080"]
