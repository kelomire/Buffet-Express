using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

const string pedidoJson = """
{
  "pedido_id": "BE-2024-001",
  "alumno": {
    "nombre": "Estudiante Ejemplo",
    "curso": "5to 1ra Informática"
  },
  "items": [
    { "id": 101, "producto": "Sánguche de Miga", "cantidad": 2, "precio_unitario": 1200 },
    { "id": 204, "producto": "Gaseosa 500ml", "cantidad": 1, "precio_unitario": 800 }
  ],
  "monto_total": 3200,
  "metodo_pago": "Efectivo",
  "estado": "PENDIENTE_CONFIRMACION"
}
""";

const string respuestaJson = """
{
  "status": 200,
  "mensaje": "Pedido recibido con éxito",
  "codigo_retiro": "BE-8842",
  "tiempo_estimado_entrega": "10:15 AM"
}
""";

var pedidoEsperado = JsonNode.Parse(pedidoJson)!;
var respuestaEsperada = JsonNode.Parse(respuestaJson)!;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");

var app = builder.Build();
app.MapPost("/pedidos", async (HttpRequest request) =>
{
    try
    {
        var pedidoRecibido = await JsonNode.ParseAsync(request.Body);
        if (!JsonNode.DeepEquals(pedidoRecibido, pedidoEsperado))
        {
            return Results.BadRequest(new { mensaje = "El pedido recibido no coincide con el esperado" });
        }

        return Results.Content(respuestaJson, "application/json", statusCode: StatusCodes.Status200OK);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { mensaje = "JSON inválido" });
    }
});

await app.StartAsync();
try
{
    var server = app.Services.GetRequiredService<IServer>();
    var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault()
        ?? throw new InvalidOperationException("No se pudo obtener la dirección del endpoint local.");

    using var client = new HttpClient();
    using var content = new StringContent(pedidoJson, System.Text.Encoding.UTF8, "application/json");
    using var response = await client.PostAsync($"{address.TrimEnd('/')}/pedidos", content);
    var respuestaRecibida = JsonNode.Parse(await response.Content.ReadAsStringAsync());

    if (response.StatusCode != HttpStatusCode.OK || !JsonNode.DeepEquals(respuestaRecibida, respuestaEsperada))
    {
        throw new InvalidOperationException($"Respuesta inesperada: {respuestaRecibida}");
    }

    Console.WriteLine("PoC OK: pedido JSON recibido y código de retiro verificado.");
    Console.WriteLine(respuestaRecibida!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    await app.StopAsync();
}