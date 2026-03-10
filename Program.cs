using Npgsql;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Use PORT env variable for cloud hosting (Render, Railway, etc.)
var port = Environment.GetEnvironmentVariable("PORT") ?? "3000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddCors();

var app = builder.Build();

app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
app.UseDefaultFiles();
app.UseStaticFiles();

// PostgreSQL connection string from environment variable
var connStr = Environment.GetEnvironmentVariable("DATABASE_URL") ?? "";

if (string.IsNullOrEmpty(connStr))
{
    Console.Error.WriteLine("ERROR: DATABASE_URL environment variable is not set.");
    Console.Error.WriteLine("Set it to your Neon PostgreSQL connection string.");
    Environment.Exit(1);
}

// Initialize database tables
using (var conn = new NpgsqlConnection(connStr))
{
    await conn.OpenAsync();
    using var cmd = new NpgsqlCommand(@"
        CREATE TABLE IF NOT EXISTS Customers (
            CustomerID SERIAL PRIMARY KEY,
            FirstName TEXT NOT NULL,
            LastName TEXT NOT NULL,
            Email TEXT NOT NULL,
            Address TEXT DEFAULT '',
            City TEXT DEFAULT '',
            State TEXT DEFAULT '',
            ZipCode TEXT DEFAULT '',
            CreatedAt TIMESTAMP DEFAULT NOW()
        );

        CREATE TABLE IF NOT EXISTS Orders (
            OrderID SERIAL PRIMARY KEY,
            OrderNumber TEXT NOT NULL UNIQUE,
            CustomerID INTEGER NOT NULL REFERENCES Customers(CustomerID),
            OrderTotal NUMERIC(10,2) NOT NULL DEFAULT 0,
            PaymentReference TEXT,
            Status TEXT DEFAULT 'pending',
            CreatedAt TIMESTAMP DEFAULT NOW()
        );

        CREATE TABLE IF NOT EXISTS OrderItems (
            ItemID SERIAL PRIMARY KEY,
            OrderID INTEGER NOT NULL REFERENCES Orders(OrderID),
            ProductName TEXT NOT NULL,
            LidType TEXT DEFAULT '',
            BaseType TEXT DEFAULT '',
            LidColor TEXT DEFAULT '',
            MidColor TEXT DEFAULT '',
            BaseColor TEXT DEFAULT '',
            Quantity INTEGER NOT NULL DEFAULT 1,
            Price NUMERIC(10,2) NOT NULL DEFAULT 0
        );
    ", conn);
    await cmd.ExecuteNonQueryAsync();
}

Console.WriteLine("Database tables ready (PostgreSQL)");

// POST /api/orders
app.MapPost("/api/orders", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);

    var customer = body.GetProperty("customer");
    var items = body.GetProperty("items");
    var orderTotal = body.TryGetProperty("orderTotal", out var ot) ? ot.GetDecimal() : 0m;

    var firstName = customer.GetProperty("firstName").GetString() ?? "";
    var lastName = customer.GetProperty("lastName").GetString() ?? "";
    var email = customer.GetProperty("email").GetString() ?? "";
    var address = customer.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "";
    var city = customer.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
    var state = customer.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
    var zip = customer.TryGetProperty("zip", out var z) ? z.GetString() ?? "" : "";

    if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName) || string.IsNullOrWhiteSpace(email))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "First name, last name, and email are required" });
        return;
    }

    try
    {
        using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();

        // Insert customer
        int customerId;
        using (var cmd = new NpgsqlCommand(@"
            INSERT INTO Customers (FirstName, LastName, Email, Address, City, State, ZipCode)
            VALUES (@fn, @ln, @em, @addr, @city, @state, @zip)
            RETURNING CustomerID", conn, tx))
        {
            cmd.Parameters.AddWithValue("@fn", firstName);
            cmd.Parameters.AddWithValue("@ln", lastName);
            cmd.Parameters.AddWithValue("@em", email);
            cmd.Parameters.AddWithValue("@addr", address);
            cmd.Parameters.AddWithValue("@city", city);
            cmd.Parameters.AddWithValue("@state", state);
            cmd.Parameters.AddWithValue("@zip", zip);
            customerId = (int)(await cmd.ExecuteScalarAsync())!;
        }

        var orderNumber = "HS-" + Random.Shared.Next(100000, 999999);

        // Insert order
        int orderId;
        using (var cmd = new NpgsqlCommand(@"
            INSERT INTO Orders (OrderNumber, CustomerID, OrderTotal, Status)
            VALUES (@on, @cid, @total, 'pending')
            RETURNING OrderID", conn, tx))
        {
            cmd.Parameters.AddWithValue("@on", orderNumber);
            cmd.Parameters.AddWithValue("@cid", customerId);
            cmd.Parameters.AddWithValue("@total", orderTotal);
            orderId = (int)(await cmd.ExecuteScalarAsync())!;
        }

        // Insert items
        for (int i = 0; i < items.GetArrayLength(); i++)
        {
            var item = items[i];
            using var cmd = new NpgsqlCommand(@"
                INSERT INTO OrderItems (OrderID, ProductName, LidType, BaseType, LidColor, MidColor, BaseColor, Quantity, Price)
                VALUES (@oid, @name, @lid, @base, @lc, @mc, @bc, @qty, @price)", conn, tx);
            cmd.Parameters.AddWithValue("@oid", orderId);
            cmd.Parameters.AddWithValue("@name", item.TryGetProperty("name", out var n) ? n.GetString() ?? "HydraStack" : "HydraStack");
            cmd.Parameters.AddWithValue("@lid", item.TryGetProperty("lidType", out var lt) ? lt.GetString() ?? "" : "");
            cmd.Parameters.AddWithValue("@base", item.TryGetProperty("baseType", out var bt) ? bt.GetString() ?? "" : "");
            cmd.Parameters.AddWithValue("@lc", item.TryGetProperty("lidColor", out var lc2) ? lc2.GetString() ?? "" : "");
            cmd.Parameters.AddWithValue("@mc", item.TryGetProperty("midColor", out var mc) ? mc.GetString() ?? "" : "");
            cmd.Parameters.AddWithValue("@bc", item.TryGetProperty("baseColor", out var bc) ? bc.GetString() ?? "" : "");
            cmd.Parameters.AddWithValue("@qty", item.TryGetProperty("quantity", out var q) ? q.GetInt32() : 1);
            cmd.Parameters.AddWithValue("@price", item.TryGetProperty("price", out var p) ? p.GetDecimal() : 0m);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        await ctx.Response.WriteAsJsonAsync(new { success = true, orderNumber, message = "Order placed successfully!" });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Order error: {ex.Message}");
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "Failed to place order. Please try again." });
    }
});

// GET /api/orders — includes items for each order
app.MapGet("/api/orders", async () =>
{
    var orders = new List<Dictionary<string, object>>();
    using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    // Get all orders with customer info
    using (var cmd = new NpgsqlCommand(@"
        SELECT o.OrderID, o.OrderNumber, o.OrderTotal, o.Status, o.CreatedAt,
               c.FirstName, c.LastName, c.Email, c.Address, c.City, c.State, c.ZipCode
        FROM Orders o
        JOIN Customers c ON o.CustomerID = c.CustomerID
        ORDER BY o.CreatedAt DESC", conn))
    {
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            orders.Add(new Dictionary<string, object>
            {
                ["orderId"] = reader.GetInt32(0),
                ["orderNumber"] = reader.GetString(1),
                ["orderTotal"] = reader.GetDecimal(2),
                ["status"] = reader.GetString(3),
                ["createdAt"] = reader.GetDateTime(4).ToString("o"),
                ["firstName"] = reader.GetString(5),
                ["lastName"] = reader.GetString(6),
                ["email"] = reader.GetString(7),
                ["address"] = reader.GetString(8),
                ["city"] = reader.GetString(9),
                ["state"] = reader.GetString(10),
                ["zip"] = reader.GetString(11),
                ["items"] = new List<object>(),
            });
        }
    }

    // Get items for each order
    foreach (var order in orders)
    {
        using var cmd = new NpgsqlCommand(@"
            SELECT ProductName, LidType, BaseType, LidColor, MidColor, BaseColor, Quantity, Price
            FROM OrderItems WHERE OrderID = @oid", conn);
        cmd.Parameters.AddWithValue("@oid", (int)order["orderId"]);

        using var reader = await cmd.ExecuteReaderAsync();
        var itemsList = (List<object>)order["items"];
        while (await reader.ReadAsync())
        {
            itemsList.Add(new
            {
                product = reader.GetString(0),
                lidType = reader.GetString(1),
                baseType = reader.GetString(2),
                lidColor = reader.GetString(3),
                midColor = reader.GetString(4),
                baseColor = reader.GetString(5),
                quantity = reader.GetInt32(6),
                price = reader.GetDecimal(7),
            });
        }
    }

    return Results.Json(orders);
});

app.Run();
