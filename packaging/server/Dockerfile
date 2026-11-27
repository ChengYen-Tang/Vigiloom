FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app/web
COPY web/ /app/web/
COPY migrator/ /app/migrator/
COPY gateway/ /app/gateway/
COPY identity/ /app/identity/
COPY resources/ /app/resources/
COPY network/ /app/network/
COPY hostmonitoring/ /app/hostmonitoring/
COPY observability/ /app/observability/
COPY operations/ /app/operations/
COPY security/ /app/security/
RUN chmod +x /app/web/Vigiloom.Web /app/migrator/Vigiloom.Migrator \
    /app/gateway/Vigiloom.Gateway /app/identity/Vigiloom.Identity /app/resources/Vigiloom.Resources \
    /app/network/Vigiloom.Network /app/hostmonitoring/Vigiloom.HostMonitoring \
    /app/observability/Vigiloom.Observability /app/operations/Vigiloom.Operations /app/security/Vigiloom.Security
USER $APP_UID
EXPOSE 7043
ENTRYPOINT ["/app/web/Vigiloom.Web"]
