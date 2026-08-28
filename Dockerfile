# One Dockerfile for all four services. They share a solution and a dependency graph, so four separate
# files would mean four copies of the same restore layer going stale independently.
ARG PROJECT

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG PROJECT
WORKDIR /src

# Restore against the manifests only, so a source change does not invalidate the package layer.
# .editorconfig comes along because analyzer severity lives in it, including the rules that are turned off
# for EF's generated migrations. Without it the image build fails on code the tooling wrote.
COPY global.json Directory.Build.props Directory.Packages.props OrderSagaSystem.slnx .editorconfig ./
COPY src/OrderSaga.AppHost/OrderSaga.AppHost.csproj src/OrderSaga.AppHost/
COPY src/OrderSaga.BuildingBlocks/OrderSaga.BuildingBlocks.csproj src/OrderSaga.BuildingBlocks/
COPY src/OrderSaga.Contracts/OrderSaga.Contracts.csproj src/OrderSaga.Contracts/
COPY src/OrderSaga.InventoryService/OrderSaga.InventoryService.csproj src/OrderSaga.InventoryService/
COPY src/OrderSaga.OrderService/OrderSaga.OrderService.csproj src/OrderSaga.OrderService/
COPY src/OrderSaga.PaymentService/OrderSaga.PaymentService.csproj src/OrderSaga.PaymentService/
COPY src/OrderSaga.ServiceDefaults/OrderSaga.ServiceDefaults.csproj src/OrderSaga.ServiceDefaults/
COPY src/OrderSaga.ShippingService/OrderSaga.ShippingService.csproj src/OrderSaga.ShippingService/
RUN dotnet restore "src/${PROJECT}/${PROJECT}.csproj"

COPY src/ src/
RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" \
    --no-restore \
    --configuration Release \
    --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

# Nothing here writes to its own filesystem or needs root.
RUN useradd --uid 64198 --create-home --shell /usr/sbin/nologin ordersaga
USER 64198

COPY --from=build /app .

ARG PROJECT
ENV ENTRYPOINT_DLL="${PROJECT}.dll"
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"${ENTRYPOINT_DLL}\""]
