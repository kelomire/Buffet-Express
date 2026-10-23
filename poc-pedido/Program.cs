using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<BuffetDb>(options =>
    options.UseSqlite("Data Source=buffet-express.db"));

builder.Services.AddScoped<PedidoService>();
builder.Services.AddRazorPages();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BuffetDb>();
    db.Database.EnsureCreated();

    if (!db.Productos.Any())
    {
        db.Productos.AddRange(
            new Producto { Nombre = "Sándwich de milanesa", Precio = 3500, Stock = 30 },
            new Producto { Nombre = "Sándwich de miga", Precio = 1200, Stock = 50 },
            new Producto { Nombre = "Gaseosa 500 ml", Precio = 800, Stock = 40 }
        );
        db.SaveChanges();
    }
}

app.UseStaticFiles();
app.MapRazorPages();

app.MapGet("/api/menu", async (BuffetDb db) =>
    await db.Productos.Where(p => p.Stock > 0).OrderBy(p => p.Nombre).ToListAsync());

app.MapPost("/api/pedidos", async (CrearPedido solicitud, PedidoService servicio) =>
{
    var resultado = await servicio.CrearAsync(solicitud);
    return resultado.Error is not null
        ? Results.BadRequest(new { mensaje = resultado.Error })
        : Results.Created($"/api/pedidos/{resultado.Pedido!.Id}", resultado.Pedido);
});

app.MapGet("/api/pedidos/{id:int}", async (int id, BuffetDb db) =>
{
    var pedido = await db.Pedidos.Include(p => p.Items)
        .ThenInclude(i => i.Producto)
        .FirstOrDefaultAsync(p => p.Id == id);

    return pedido is null ? Results.NotFound() : Results.Ok(pedido);
});

app.MapGet("/api/cocina/pedidos", async (BuffetDb db) =>
    await db.Pedidos.Include(p => p.Items)
        .ThenInclude(i => i.Producto)
        .OrderByDescending(p => p.Fecha)
        .ToListAsync());

app.MapPatch("/api/pedidos/{id:int}/estado", async (int id, CambiarEstado solicitud, BuffetDb db) =>
{
    var estados = new[] { "Pendiente", "EnPreparacion", "ListoParaRetirar", "Entregado", "Cancelado" };

    if (!estados.Contains(solicitud.Estado))
        return Results.BadRequest(new { mensaje = "Estado inválido" });

    var pedido = await db.Pedidos.FindAsync(id);
    if (pedido is null)
        return Results.NotFound();

    pedido.Estado = solicitud.Estado;
    await db.SaveChangesAsync();
    return Results.Ok(pedido);
});

app.MapGet("/", () => Results.Redirect("/Alumno"));

app.Run();

public class BuffetDb(DbContextOptions<BuffetDb> options) : DbContext(options)
{
    public DbSet<Producto> Productos => Set<Producto>();
    public DbSet<Pedido> Pedidos => Set<Pedido>();
    public DbSet<ItemPedido> ItemsPedido => Set<ItemPedido>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Producto>().Property(p => p.Precio).HasPrecision(10, 2);
        modelBuilder.Entity<Pedido>().Property(p => p.Total).HasPrecision(10, 2);
        modelBuilder.Entity<ItemPedido>().Property(i => i.PrecioUnitario).HasPrecision(10, 2);
    }
}

public class Producto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public decimal Precio { get; set; }
    public int Stock { get; set; }
}

public class Pedido
{
    public int Id { get; set; }
    public string Codigo { get; set; } = "";
    public string NombreAlumno { get; set; } = "";
    public string Curso { get; set; } = "";
    public string Recreo { get; set; } = "";
    public string Estado { get; set; } = "Pendiente";
    public decimal Total { get; set; }
    public DateTime Fecha { get; set; } = DateTime.UtcNow;
    public List<ItemPedido> Items { get; set; } = [];
}

public class ItemPedido
{
    public int Id { get; set; }
    public int PedidoId { get; set; }
    public Pedido? Pedido { get; set; }
    public int ProductoId { get; set; }
    public Producto? Producto { get; set; }
    public int Cantidad { get; set; }
    public decimal PrecioUnitario { get; set; }
}

public record ItemSolicitado(int ProductoId, int Cantidad);
public record CrearPedido(string NombreAlumno, string Curso, string Recreo, List<ItemSolicitado>? Items);
public record CambiarEstado(string Estado);

public class PedidoService(BuffetDb db)
{
    public async Task<(Pedido? Pedido, string? Error)> CrearAsync(CrearPedido solicitud)
    {
        if (string.IsNullOrWhiteSpace(solicitud.NombreAlumno) ||
            string.IsNullOrWhiteSpace(solicitud.Curso))
            return (null, "Completá el nombre y el curso.");

        if (solicitud.Recreo is not ("Recreo 1" or "Recreo 2" or "Recreo 3"))
            return (null, "Seleccioná un recreo válido.");

        if (solicitud.Items is null || solicitud.Items.Count == 0)
            return (null, "Agregá al menos un producto.");

        if (solicitud.Items.Any(i => i.Cantidad < 1 || i.Cantidad > 20))
            return (null, "La cantidad debe estar entre 1 y 20.");

        await using var transaccion = await db.Database.BeginTransactionAsync();

        var ids = solicitud.Items.Select(i => i.ProductoId).Distinct().ToList();
        var productos = await db.Productos.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        foreach (var item in solicitud.Items)
        {
            if (!productos.TryGetValue(item.ProductoId, out var producto))
                return (null, $"No existe el producto {item.ProductoId}.");

            if (producto.Stock < item.Cantidad)
                return (null, $"No hay stock suficiente de {producto.Nombre}.");
        }

        var pedido = new Pedido
        {
            NombreAlumno = solicitud.NombreAlumno.Trim(),
            Curso = solicitud.Curso.Trim(),
            Recreo = solicitud.Recreo,
            Estado = "Pendiente"
        };

        foreach (var item in solicitud.Items)
        {
            var producto = productos[item.ProductoId];
            producto.Stock -= item.Cantidad;

            pedido.Items.Add(new ItemPedido
            {
                ProductoId = producto.Id,
                Cantidad = item.Cantidad,
                PrecioUnitario = producto.Precio
            });

            pedido.Total += producto.Precio * item.Cantidad;
        }

        db.Pedidos.Add(pedido);
        await db.SaveChangesAsync();
        pedido.Codigo = $"BE-{pedido.Id:D6}";
        await db.SaveChangesAsync();
        await transaccion.CommitAsync();

        return (pedido, null);
    }
}
