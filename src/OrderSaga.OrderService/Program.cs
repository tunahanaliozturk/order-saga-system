using OrderSaga.OrderService;

WebApplication app = await OrderServiceHost.BuildAsync(args);
await app.RunAsync();
