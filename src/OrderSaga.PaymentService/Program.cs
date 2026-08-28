using OrderSaga.PaymentService;

WebApplication app = await PaymentServiceHost.BuildAsync(args);
await app.RunAsync();
