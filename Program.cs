using Npgsql;
using System.Text.Json;
using System.Text.RegularExpressions;

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
var rawConnStr = Environment.GetEnvironmentVariable("DATABASE_URL") ?? "";

if (string.IsNullOrEmpty(rawConnStr))
{
    Console.Error.WriteLine("ERROR: DATABASE_URL environment variable is not set.");
    Console.Error.WriteLine("Set it to your Neon PostgreSQL connection string.");
    Environment.Exit(1);
}

// Convert postgresql:// URI to Npgsql key-value format
var dbUri = new Uri(rawConnStr);
var userInfo = dbUri.UserInfo.Split(':');
var connStr = $"Host={dbUri.Host};Port={(dbUri.Port > 0 ? dbUri.Port : 5432)};Database={dbUri.AbsolutePath.TrimStart('/')};Username={userInfo[0]};Password={Uri.UnescapeDataString(userInfo[1])};SSL Mode=Require;Trust Server Certificate=true";

// Email validation helper
var emailRegex = new Regex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.Compiled);

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
            UserID INTEGER,
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

        CREATE TABLE IF NOT EXISTS Users (
            UserID SERIAL PRIMARY KEY,
            Username TEXT NOT NULL UNIQUE,
            PasswordHash TEXT NOT NULL,
            CreatedAt TIMESTAMP DEFAULT NOW()
        );

        CREATE TABLE IF NOT EXISTS SavedBuilds (
            SavedBuildID SERIAL PRIMARY KEY,
            UserID INTEGER NOT NULL REFERENCES Users(UserID),
            BuildName TEXT DEFAULT 'My Build',
            LidType TEXT NOT NULL,
            BaseType TEXT NOT NULL,
            LidColor TEXT NOT NULL,
            MidColor TEXT NOT NULL,
            BaseColor TEXT NOT NULL,
            IsShared BOOLEAN DEFAULT FALSE,
            CreatedAt TIMESTAMP DEFAULT NOW()
        );

        CREATE TABLE IF NOT EXISTS Reviews (
            ReviewID SERIAL PRIMARY KEY,
            Name TEXT NOT NULL,
            Email TEXT DEFAULT '',
            Rating INTEGER NOT NULL,
            Message TEXT NOT NULL,
            CreatedAt TIMESTAMP DEFAULT NOW()
        );
    ", conn);
    await cmd.ExecuteNonQueryAsync();

    // Add UserID column to Orders if it doesn't exist (for existing databases)
    using var alterCmd = new NpgsqlCommand(@"
        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='orders' AND column_name='userid') THEN
                ALTER TABLE Orders ADD COLUMN UserID INTEGER;
            END IF;
        END $$;
    ", conn);
    await alterCmd.ExecuteNonQueryAsync();

    // Add MidType column to SavedBuilds if it doesn't exist (for existing databases)
    using var alterCmd2 = new NpgsqlCommand(@"
        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='savedbuilds' AND column_name='midtype') THEN
                ALTER TABLE SavedBuilds ADD COLUMN MidType TEXT NOT NULL DEFAULT 'standard';
            END IF;
        END $$;
    ", conn);
    await alterCmd2.ExecuteNonQueryAsync();
}

Console.WriteLine("Database tables ready (PostgreSQL)");

// POST /api/auth/register
app.MapPost("/api/auth/register", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);

    var username = body.TryGetProperty("username", out var u) ? u.GetString()?.Trim() ?? "" : "";
    var password = body.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";

    if (string.IsNullOrWhiteSpace(username) || username.Length < 3)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Username must be at least 3 characters" });
        return;
    }

    if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Password must be at least 6 characters" });
        return;
    }

    try
    {
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(password);

        using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        using var cmd = new NpgsqlCommand(@"
            INSERT INTO Users (Username, PasswordHash)
            VALUES (@username, @hash)
            RETURNING UserID", conn);
        cmd.Parameters.AddWithValue("@username", username);
        cmd.Parameters.AddWithValue("@hash", passwordHash);

        var userId = (int)(await cmd.ExecuteScalarAsync())!;
        await ctx.Response.WriteAsJsonAsync(new { success = true, user = new { userId, username } });
    }
    catch (PostgresException ex) when (ex.SqlState == "23505") // unique violation
    {
        ctx.Response.StatusCode = 409;
        await ctx.Response.WriteAsJsonAsync(new { error = "Username already taken" });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Register error: {ex.Message}");
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "Registration failed. Please try again." });
    }
});

// POST /api/auth/login
app.MapPost("/api/auth/login", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);

    var username = body.TryGetProperty("username", out var u) ? u.GetString()?.Trim() ?? "" : "";
    var password = body.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";

    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Username and password are required" });
        return;
    }

    try
    {
        using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        using var cmd = new NpgsqlCommand(@"
            SELECT UserID, Username, PasswordHash FROM Users WHERE Username = @username", conn);
        cmd.Parameters.AddWithValue("@username", username);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "Invalid username or password" });
            return;
        }

        var userId = reader.GetInt32(0);
        var dbUsername = reader.GetString(1);
        var hash = reader.GetString(2);

        if (!BCrypt.Net.BCrypt.Verify(password, hash))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "Invalid username or password" });
            return;
        }

        await ctx.Response.WriteAsJsonAsync(new { success = true, user = new { userId, username = dbUsername } });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Login error: {ex.Message}");
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "Login failed. Please try again." });
    }
});

// GET /api/auth/orders/{userId}
app.MapGet("/api/auth/orders/{userId:int}", async (int userId) =>
{
    var orders = new List<Dictionary<string, object>>();
    using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    using (var cmd = new NpgsqlCommand(@"
        SELECT o.OrderID, o.OrderNumber, o.OrderTotal, o.Status, o.CreatedAt,
               c.FirstName, c.LastName, c.Email
        FROM Orders o
        JOIN Customers c ON o.CustomerID = c.CustomerID
        WHERE o.UserID = @uid
        ORDER BY o.CreatedAt DESC", conn))
    {
        cmd.Parameters.AddWithValue("@uid", userId);
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
                ["items"] = new List<object>(),
            });
        }
    }

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

// POST /api/orders
app.MapPost("/api/orders", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);

    var customer = body.GetProperty("customer");
    var items = body.GetProperty("items");
    var orderTotal = body.TryGetProperty("orderTotal", out var ot) ? ot.GetDecimal() : 0m;
    var userId = body.TryGetProperty("userId", out var uid) ? uid.GetInt32() : (int?)null;

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

    if (!emailRegex.IsMatch(email))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Invalid email address" });
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

        // Insert order (with optional UserID)
        int orderId;
        using (var cmd = new NpgsqlCommand(@"
            INSERT INTO Orders (OrderNumber, CustomerID, OrderTotal, Status, UserID)
            VALUES (@on, @cid, @total, 'pending', @uid)
            RETURNING OrderID", conn, tx))
        {
            cmd.Parameters.AddWithValue("@on", orderNumber);
            cmd.Parameters.AddWithValue("@cid", customerId);
            cmd.Parameters.AddWithValue("@total", orderTotal);
            cmd.Parameters.AddWithValue("@uid", userId.HasValue ? userId.Value : DBNull.Value);
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

// GET /api/orders — includes items for each order (admin)
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

// === SAVED BUILDS API ===

// POST /api/builds — Save a build
app.MapPost("/api/builds", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);

    var userId = body.TryGetProperty("userId", out var uid) ? uid.GetInt32() : 0;
    var buildName = body.TryGetProperty("buildName", out var bn) ? bn.GetString()?.Trim() ?? "My Build" : "My Build";
    var lidType = body.TryGetProperty("lidType", out var lt) ? lt.GetString() ?? "" : "";
    var midType = body.TryGetProperty("midType", out var mt) ? mt.GetString() ?? "standard" : "standard";
    var baseType = body.TryGetProperty("baseType", out var bt) ? bt.GetString() ?? "" : "";
    var lidColor = body.TryGetProperty("lidColor", out var lc) ? lc.GetString() ?? "" : "";
    var midColor = body.TryGetProperty("midColor", out var mc) ? mc.GetString() ?? "" : "";
    var baseColor = body.TryGetProperty("baseColor", out var bc) ? bc.GetString() ?? "" : "";

    if (userId == 0)
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new { error = "Please sign in to save builds." });
        return;
    }

    if (string.IsNullOrWhiteSpace(lidType) || string.IsNullOrWhiteSpace(baseType))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Invalid build configuration." });
        return;
    }

    try
    {
        using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        using var cmd = new NpgsqlCommand(@"
            INSERT INTO SavedBuilds (UserID, BuildName, LidType, MidType, BaseType, LidColor, MidColor, BaseColor)
            VALUES (@uid, @name, @lid, @mid, @base, @lc, @mc, @bc)
            RETURNING SavedBuildID", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@name", buildName);
        cmd.Parameters.AddWithValue("@lid", lidType);
        cmd.Parameters.AddWithValue("@mid", midType);
        cmd.Parameters.AddWithValue("@base", baseType);
        cmd.Parameters.AddWithValue("@lc", lidColor);
        cmd.Parameters.AddWithValue("@mc", midColor);
        cmd.Parameters.AddWithValue("@bc", baseColor);

        var buildId = (int)(await cmd.ExecuteScalarAsync())!;
        await ctx.Response.WriteAsJsonAsync(new { success = true, buildId });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Save build error: {ex.Message}");
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "Failed to save build." });
    }
});

// GET /api/builds/{userId} — Get user's saved builds
app.MapGet("/api/builds/{userId:int}", async (int userId) =>
{
    var builds = new List<object>();
    using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    using var cmd = new NpgsqlCommand(@"
        SELECT SavedBuildID, BuildName, LidType, MidType, BaseType, LidColor, MidColor, BaseColor, IsShared, CreatedAt
        FROM SavedBuilds WHERE UserID = @uid
        ORDER BY CreatedAt DESC", conn);
    cmd.Parameters.AddWithValue("@uid", userId);

    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        builds.Add(new
        {
            buildId = reader.GetInt32(0),
            buildName = reader.GetString(1),
            lidType = reader.GetString(2),
            midType = reader.GetString(3),
            baseType = reader.GetString(4),
            lidColor = reader.GetString(5),
            midColor = reader.GetString(6),
            baseColor = reader.GetString(7),
            isShared = reader.GetBoolean(8),
            createdAt = reader.GetDateTime(9).ToString("o"),
        });
    }

    return Results.Json(builds);
});

// DELETE /api/builds/{buildId}
app.MapDelete("/api/builds/{buildId:int}", async (int buildId) =>
{
    try
    {
        using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        using var cmd = new NpgsqlCommand("DELETE FROM SavedBuilds WHERE SavedBuildID = @id", conn);
        cmd.Parameters.AddWithValue("@id", buildId);
        await cmd.ExecuteNonQueryAsync();

        return Results.Json(new { success = true });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Delete build error: {ex.Message}");
        return Results.Json(new { error = "Failed to delete build." });
    }
});

// PUT /api/builds/{buildId}/share — Toggle share status
app.MapPut("/api/builds/{buildId:int}/share", async (int buildId) =>
{
    try
    {
        using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        using var cmd = new NpgsqlCommand(@"
            UPDATE SavedBuilds SET IsShared = NOT IsShared WHERE SavedBuildID = @id
            RETURNING IsShared", conn);
        cmd.Parameters.AddWithValue("@id", buildId);

        var result = await cmd.ExecuteScalarAsync();
        return Results.Json(new { success = true, isShared = (bool)result! });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Share toggle error: {ex.Message}");
        return Results.Json(new { error = "Failed to update share status." });
    }
});

// GET /api/builds/community — Get all shared builds
app.MapGet("/api/builds/community", async () =>
{
    var builds = new List<object>();
    using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    using var cmd = new NpgsqlCommand(@"
        SELECT sb.SavedBuildID, sb.BuildName, sb.LidType, sb.MidType, sb.BaseType, sb.LidColor, sb.MidColor, sb.BaseColor, sb.CreatedAt, u.Username
        FROM SavedBuilds sb
        JOIN Users u ON sb.UserID = u.UserID
        WHERE sb.IsShared = TRUE
        ORDER BY sb.CreatedAt DESC
        LIMIT 50", conn);

    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        builds.Add(new
        {
            buildId = reader.GetInt32(0),
            buildName = reader.GetString(1),
            lidType = reader.GetString(2),
            midType = reader.GetString(3),
            baseType = reader.GetString(4),
            lidColor = reader.GetString(5),
            midColor = reader.GetString(6),
            baseColor = reader.GetString(7),
            createdAt = reader.GetDateTime(8).ToString("o"),
            username = reader.GetString(9),
        });
    }

    return Results.Json(builds);
});

// === REVIEWS API ===

// POST /api/reviews — Submit a review
app.MapPost("/api/reviews", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);

    var name = body.TryGetProperty("name", out var n) ? n.GetString()?.Trim() ?? "" : "";
    var email = body.TryGetProperty("email", out var e) ? e.GetString()?.Trim() ?? "" : "";
    var rating = body.TryGetProperty("rating", out var r) ? r.GetInt32() : 0;
    var message = body.TryGetProperty("message", out var m) ? m.GetString()?.Trim() ?? "" : "";

    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(message))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Name and message are required." });
        return;
    }

    if (rating < 1 || rating > 5)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Rating must be between 1 and 5." });
        return;
    }

    try
    {
        using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        using var cmd = new NpgsqlCommand(@"
            INSERT INTO Reviews (Name, Email, Rating, Message)
            VALUES (@name, @email, @rating, @msg)
            RETURNING ReviewID", conn);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@email", email);
        cmd.Parameters.AddWithValue("@rating", rating);
        cmd.Parameters.AddWithValue("@msg", message);

        var reviewId = (int)(await cmd.ExecuteScalarAsync())!;
        await ctx.Response.WriteAsJsonAsync(new { success = true, reviewId });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Review error: {ex.Message}");
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "Failed to submit review." });
    }
});

// GET /api/reviews — All reviews (admin)
app.MapGet("/api/reviews", async () =>
{
    var reviews = new List<object>();
    using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    using var cmd = new NpgsqlCommand(@"
        SELECT ReviewID, Name, Email, Rating, Message, CreatedAt
        FROM Reviews ORDER BY CreatedAt DESC", conn);

    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        reviews.Add(new
        {
            reviewId = reader.GetInt32(0),
            name = reader.GetString(1),
            email = reader.GetString(2),
            rating = reader.GetInt32(3),
            message = reader.GetString(4),
            createdAt = reader.GetDateTime(5).ToString("o"),
        });
    }

    return Results.Json(reviews);
});

// GET /api/reviews/recent — Recent reviews for display
app.MapGet("/api/reviews/recent", async () =>
{
    var reviews = new List<object>();
    using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    using var cmd = new NpgsqlCommand(@"
        SELECT Name, Rating, Message, CreatedAt
        FROM Reviews ORDER BY CreatedAt DESC LIMIT 10", conn);

    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        reviews.Add(new
        {
            name = reader.GetString(0),
            rating = reader.GetInt32(1),
            message = reader.GetString(2),
            createdAt = reader.GetDateTime(3).ToString("o"),
        });
    }

    return Results.Json(reviews);
});

app.Run();
