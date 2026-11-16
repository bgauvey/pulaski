# syntax=docker/dockerfile:1
# Pulaski database setup: a one-shot container that creates or upgrades the database, then exits. Build from the repository root:
# docker build -f deploy/images/db/Dockerfile .  It connects with the usual PG* variables; see init.sh.
FROM postgres:18
COPY db/ /pulaski/db/
COPY deploy/images/db/init.sh /pulaski/init.sh
ENTRYPOINT ["bash", "/pulaski/init.sh"]
