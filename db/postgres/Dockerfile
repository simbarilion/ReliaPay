# syntax=docker/dockerfile:1

FROM postgres:16@sha256:081f1bc7bd5e143dbb6e487b710bbc27712cdcfaced4c071b8e47349aa1b4171

COPY db/init /docker-entrypoint-initdb.d
COPY db/postgres/ensure-app-roles.sh /usr/local/bin/ensure-app-roles.sh
COPY db/postgres/entrypoint.sh /usr/local/bin/reliapay-pg-entrypoint.sh

RUN chmod +x /usr/local/bin/ensure-app-roles.sh /usr/local/bin/reliapay-pg-entrypoint.sh \
    && chmod +x /docker-entrypoint-initdb.d/*.sh

ENTRYPOINT ["/usr/local/bin/reliapay-pg-entrypoint.sh"]
CMD ["postgres"]
