FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app/web
COPY web/ /app/web/
COPY worker/ /app/worker/
COPY migrator/ /app/migrator/
RUN chmod +x /app/web/Vigiloom.Web /app/worker/Vigiloom.Worker /app/migrator/Vigiloom.Migrator
USER $APP_UID
EXPOSE 7043
ENTRYPOINT ["/app/web/Vigiloom.Web"]
