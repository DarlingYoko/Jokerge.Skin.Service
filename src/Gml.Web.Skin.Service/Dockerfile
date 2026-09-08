FROM mcr.microsoft.com/dotnet/aspnet:8.0@sha256:6f4606cc4deb16d9e7f9003393cb1e41fefa23ebeb7dec6dd4c99dee0eacb55a AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0@sha256:237133a0ea20cffcfaa92588e1c8a56d58fe99f44da72dcff35aeb017119abcf AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/Gml.Web.Skin.Service/Gml.Web.Skin.Service.csproj", "src/Gml.Web.Skin.Service/"]
RUN dotnet restore "src/Gml.Web.Skin.Service/Gml.Web.Skin.Service.csproj"
COPY . .
WORKDIR "/src/src/Gml.Web.Skin.Service"
RUN dotnet build "Gml.Web.Skin.Service.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Gml.Web.Skin.Service.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Gml.Web.Skin.Service.dll"]
