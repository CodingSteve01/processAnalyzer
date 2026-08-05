# The miner runs apart from the application on purpose: pm4py is AGPL-3.0 and drags in a scientific stack an
# ASP.NET image has no business carrying. It publishes no ports and is never reachable from outside.
FROM python:3.12-slim

# graphviz is a system dependency, not a wheel — without the binary every render fails at the last step.
RUN apt-get update \
    && apt-get install -y --no-install-recommends graphviz \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY mine.py .
ENV PROCESSANALYZER_ARTIFACTS=/artifacts
ENTRYPOINT ["python", "mine.py"]
