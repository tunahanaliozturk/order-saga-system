using OrderSaga.ShippingService;

WebApplication app = await ShippingServiceHost.BuildAsync(args);
await app.RunAsync();
