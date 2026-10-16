# Game server (ASP.NET Core + SignalR). Build context: repo root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY TicTacToeUltimate.Contracts/ TicTacToeUltimate.Contracts/
COPY TicTacToeUltimate.Engine/ TicTacToeUltimate.Engine/
COPY TicTacToeUltimate.Server/ TicTacToeUltimate.Server/
RUN dotnet publish TicTacToeUltimate.Server -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080 ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "TicTacToeUltimate.Server.dll"]
