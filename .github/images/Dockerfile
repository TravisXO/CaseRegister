# Not built. Dependabot reads the Dockerfiles it is pointed at, and cannot read an image named in a workflow, so the PostgreSQL image the
# backup workflows use is named here too: when it offers a new digest for this line, the same digest goes into backup.yml and
# restore-check.yml (a test fails until they agree).
FROM postgres:18@sha256:74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336
