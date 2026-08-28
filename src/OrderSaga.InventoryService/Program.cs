using OrderSaga.InventoryService;

WebApplication app = await InventoryServiceHost.BuildAsync(args);
await app.RunAsync();
