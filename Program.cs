using Npgsql;
using System.Security.Cryptography;
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

// Resend email config
var resendApiKey = Environment.GetEnvironmentVariable("RESEND_API_KEY") ?? "";
var emailFrom = Environment.GetEnvironmentVariable("EMAIL_FROM") ?? "onboarding@resend.dev";
var httpClient = new HttpClient();

if (string.IsNullOrEmpty(resendApiKey))
{
    Console.WriteLine("WARNING: RESEND_API_KEY not set. Password reset emails will not be sent.");
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

    // Create BuildVotes table for upvote/downvote on community builds
    using var votesCmd = new NpgsqlCommand(@"
        CREATE TABLE IF NOT EXISTS BuildVotes (
            VoteID SERIAL PRIMARY KEY,
            BuildID INTEGER NOT NULL REFERENCES SavedBuilds(SavedBuildID) ON DELETE CASCADE,
            VoterIP TEXT NOT NULL,
            Direction TEXT NOT NULL DEFAULT 'up',
            CreatedAt TIMESTAMP DEFAULT NOW(),
            UNIQUE(BuildID, VoterIP)
        );
    ", conn);
    await votesCmd.ExecuteNonQueryAsync();

    // Add Email column to Users if it doesn't exist
    using var alterEmailCmd = new NpgsqlCommand(@"
        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='email') THEN
                ALTER TABLE Users ADD COLUMN Email TEXT DEFAULT '';
            END IF;
        END $$;
    ", conn);
    await alterEmailCmd.ExecuteNonQueryAsync();

    // Create PasswordResetTokens table
    using var resetTokensCmd = new NpgsqlCommand(@"
        CREATE TABLE IF NOT EXISTS PasswordResetTokens (
            TokenID SERIAL PRIMARY KEY,
            UserID INTEGER NOT NULL REFERENCES Users(UserID),
            Token TEXT NOT NULL UNIQUE,
            ExpiresAt TIMESTAMP NOT NULL,
            Used BOOLEAN DEFAULT FALSE,
            CreatedAt TIMESTAMP DEFAULT NOW()
        );
    ", conn);
    await resetTokensCmd.ExecuteNonQueryAsync();
}

Console.WriteLine("Database tables ready (PostgreSQL)");

// === PROFANITY FILTER ===
var bannedWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    // Slurs & hate speech
    "nigger","nigga","nigg3r","n1gger","n1gga","nigg","nig","negro","negr0",
    "faggot","fagg0t","f4ggot","fag","f4g","dyke","dyk3",
    "retard","r3tard","retrd","spic","sp1c","spick","sp1ck",
    "chink","ch1nk","gook","g00k","wetback","w3tback",
    "kike","k1ke","beaner","b3aner","coon","c00n","darkie","dark1e",
    "cracker","honky","h0nky","gringo","gring0",
    "tranny","tr4nny","shemale","sh3male",
    "raghead","r4ghead","towelhead","t0welhead","camel jockey",
    "zipperhead","jigaboo","sambo","pickaninny","uncle tom",
    "halfbreed","mongrel","mutt",

    // Profanity
    "fuck","f*ck","fck","fuk","fuq","fuc","f**k","fu*k","f u c k",
    "shit","sh1t","sh!t","s**t","sht","shyt",
    "bitch","b1tch","b!tch","b*tch","biatch","bytch",
    "ass","a$$","@ss","a**","asshole","a$$hole","assh0le",
    "damn","d4mn","dmn","damnit",
    "dick","d1ck","d!ck","d*ck","dik",
    "cock","c0ck","c*ck","cok",
    "pussy","pu$$y","puss","pus$y","p*ssy",
    "cunt","c*nt","cnut","cvnt","c**t",
    "whore","wh0re","wh*re","h0e","hoe",
    "slut","sl*t","s1ut",
    "bastard","b4stard","b@stard",
    "penis","pen1s","pen!s",
    "vagina","vag1na","vag",
    "tits","t1ts","titties","t!ts","boobs","b00bs",
    "cum","c*m","jizz","j1zz",
    "dildo","d1ldo","dild0",
    "porn","p0rn","pr0n","porno","p0rno",
    "anal","an4l","anus","@nus",
    "blowjob","bl0wjob","bj",
    "handjob","h4ndjob",
    "masturbat","masturb8","wank","w4nk",
    "erection","boner","b0ner",
    "orgasm","0rgasm",
    "semen","s3men","sperm","sp3rm",
    "testicle","test1cle","ballsack","ballsak","nutsack",
    "butthole","butth0le","buttplug",

    // Offensive / harassment
    "nazi","n4zi","naz1","hitler","h1tler","heil","h3il",
    "kkk","klan","ku klux",
    "rape","r4pe","rap3","rapist","rap1st",
    "pedo","p3do","ped0","pedophile","paedo",
    "molest","mol3st","m0lest",
    "suicide","su1cide","kys","k.y.s","killyourself","killurself",
    "terrorist","terr0rist",
    "bomb","b0mb",
    "shoot","sh00t",
    "murder","murd3r",
    "genocide","gen0cide",

    // Drugs
    "meth","m3th","heroin","her0in","cocaine","cocain3","crack","cr4ck",

    // Common evasions
    "stfu","gtfo","lmfao","milf","thot","th0t","incel","1ncel",
    "onlyfans","0nlyfans",
};

// Check if text contains any banned word (whole word or substring)
bool ContainsProfanity(string text)
{
    if (string.IsNullOrWhiteSpace(text)) return false;

    // Normalize: lowercase, strip common leet substitutions
    var normalized = text.ToLowerInvariant()
        .Replace("0", "o").Replace("1", "i").Replace("3", "e")
        .Replace("4", "a").Replace("5", "s").Replace("7", "t")
        .Replace("@", "a").Replace("!", "i").Replace("$", "s")
        .Replace(" ", "").Replace(".", "").Replace("-", "").Replace("_", "");

    var lower = text.ToLowerInvariant();

    foreach (var word in bannedWords)
    {
        var w = word.ToLowerInvariant();
        // Check raw text (with spaces)
        if (lower.Contains(w)) return true;
        // Check normalized (leet-speak stripped, no spaces)
        var normalizedWord = w.Replace(" ", "").Replace(".", "").Replace("-", "").Replace("_", "")
            .Replace("0", "o").Replace("1", "i").Replace("3", "e")
            .Replace("4", "a").Replace("5", "s").Replace("7", "t")
            .Replace("@", "a").Replace("!", "i").Replace("$", "s");
        if (normalized.Contains(normalizedWord)) return true;
    }
    return false;
}

// POST /api/auth/register
app.MapPost("/api/auth/register", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);

    var username = body.TryGetProperty("username", out var u) ? u.GetString()?.Trim() ?? "" : "";
    var password = body.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
    var email = body.TryGetProperty("email", out var e) ? e.GetString()?.Trim() ?? "" : "";

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

    if (string.IsNullOrWhiteSpace(email) || !emailRegex.IsMatch(email))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "A valid email address is required" });
        return;
    }

    if (ContainsProfanity(username))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Username contains inappropriate language. Please choose a different name." });
        return;
    }

    try
    {
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(password);

        using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        using var cmd = new NpgsqlCommand(@"
            INSERT INTO Users (Username, PasswordHash, Email)
            VALUES (@username, @hash, @email)
            RETURNING UserID", conn);
        cmd.Parameters.AddWithValue("@username", username);
        cmd.Parameters.AddWithValue("@hash", passwordHash);
        cmd.Parameters.AddWithValue("@email", email.ToLowerInvariant());

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

    if (ContainsProfanity(buildName))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Build name contains inappropriate language. Please choose a different name." });
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

// GET /api/builds/community — Get all shared builds with vote counts
app.MapGet("/api/builds/community", async () =>
{
    var builds = new List<object>();
    using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    using var cmd = new NpgsqlCommand(@"
        SELECT sb.SavedBuildID, sb.BuildName, sb.LidType, sb.MidType, sb.BaseType, sb.LidColor, sb.MidColor, sb.BaseColor, sb.CreatedAt, u.Username,
            COALESCE(SUM(CASE WHEN bv.Direction = 'up' THEN 1 ELSE 0 END), 0) AS Upvotes,
            COALESCE(SUM(CASE WHEN bv.Direction = 'down' THEN 1 ELSE 0 END), 0) AS Downvotes
        FROM SavedBuilds sb
        JOIN Users u ON sb.UserID = u.UserID
        LEFT JOIN BuildVotes bv ON sb.SavedBuildID = bv.BuildID
        WHERE sb.IsShared = TRUE
        GROUP BY sb.SavedBuildID, sb.BuildName, sb.LidType, sb.MidType, sb.BaseType, sb.LidColor, sb.MidColor, sb.BaseColor, sb.CreatedAt, u.Username
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
            upvotes = reader.GetInt64(10),
            downvotes = reader.GetInt64(11),
        });
    }

    return Results.Json(builds);
});

// POST /api/builds/{id}/vote — Upvote or downvote a community build
app.MapPost("/api/builds/{id}/vote", async (HttpContext ctx, int id) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
    var direction = body.TryGetProperty("direction", out var d) ? d.GetString() ?? "" : "";

    // Use IP as a simple anonymous voter identifier
    var voterIP = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    // Check for forwarded header
    if (ctx.Request.Headers.ContainsKey("X-Forwarded-For"))
    {
        voterIP = ctx.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
    }

    try
    {
        using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        if (direction == "none" || string.IsNullOrEmpty(direction))
        {
            // Remove vote
            using var delCmd = new NpgsqlCommand("DELETE FROM BuildVotes WHERE BuildID = @bid AND VoterIP = @ip", conn);
            delCmd.Parameters.AddWithValue("@bid", id);
            delCmd.Parameters.AddWithValue("@ip", voterIP);
            await delCmd.ExecuteNonQueryAsync();
        }
        else if (direction == "up" || direction == "down")
        {
            // Upsert vote
            using var upsertCmd = new NpgsqlCommand(@"
                INSERT INTO BuildVotes (BuildID, VoterIP, Direction)
                VALUES (@bid, @ip, @dir)
                ON CONFLICT (BuildID, VoterIP) DO UPDATE SET Direction = @dir", conn);
            upsertCmd.Parameters.AddWithValue("@bid", id);
            upsertCmd.Parameters.AddWithValue("@ip", voterIP);
            upsertCmd.Parameters.AddWithValue("@dir", direction);
            await upsertCmd.ExecuteNonQueryAsync();
        }

        // Return updated counts
        using var countCmd = new NpgsqlCommand(@"
            SELECT
                COALESCE(SUM(CASE WHEN Direction = 'up' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN Direction = 'down' THEN 1 ELSE 0 END), 0)
            FROM BuildVotes WHERE BuildID = @bid", conn);
        countCmd.Parameters.AddWithValue("@bid", id);
        using var reader = await countCmd.ExecuteReaderAsync();
        await reader.ReadAsync();

        return Results.Json(new { success = true, upvotes = reader.GetInt64(0), downvotes = reader.GetInt64(1) });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Vote error: {ex.Message}");
        return Results.Json(new { error = "Failed to record vote." });
    }
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

    if (ContainsProfanity(name) || ContainsProfanity(message))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Your feedback contains inappropriate language. Please revise and try again." });
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

// GET /api/admin/users - List all users
app.MapGet("/api/admin/users", async () =>
{
    var users = new List<object>();
    using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    using var cmd = new NpgsqlCommand(@"
        SELECT UserID, Username, COALESCE(Email, '') as Email, CreatedAt FROM Users ORDER BY CreatedAt DESC", conn);
    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        users.Add(new
        {
            userId = reader.GetInt32(0),
            username = reader.GetString(1),
            email = reader.GetString(2),
            createdAt = reader.GetDateTime(3).ToString("o"),
        });
    }
    return Results.Json(users);
});

// DELETE /api/admin/users/{userId} - Delete a user and their related data
app.MapDelete("/api/admin/users/{userId:int}", async (int userId) =>
{
    using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    // Delete in order of foreign key dependencies
    using var deleteVotes = new NpgsqlCommand(@"
        DELETE FROM BuildVotes WHERE BuildID IN (SELECT SavedBuildID FROM SavedBuilds WHERE UserID = @userId)", conn);
    deleteVotes.Parameters.AddWithValue("@userId", userId);
    await deleteVotes.ExecuteNonQueryAsync();

    using var deleteBuilds = new NpgsqlCommand(@"DELETE FROM SavedBuilds WHERE UserID = @userId", conn);
    deleteBuilds.Parameters.AddWithValue("@userId", userId);
    await deleteBuilds.ExecuteNonQueryAsync();

    using var deleteTokens = new NpgsqlCommand(@"DELETE FROM PasswordResetTokens WHERE UserID = @userId", conn);
    deleteTokens.Parameters.AddWithValue("@userId", userId);
    await deleteTokens.ExecuteNonQueryAsync();

    // Unlink orders (keep order data, just remove user association)
    using var unlinkOrders = new NpgsqlCommand(@"UPDATE Orders SET UserID = NULL WHERE UserID = @userId", conn);
    unlinkOrders.Parameters.AddWithValue("@userId", userId);
    await unlinkOrders.ExecuteNonQueryAsync();

    using var deleteUser = new NpgsqlCommand(@"DELETE FROM Users WHERE UserID = @userId", conn);
    deleteUser.Parameters.AddWithValue("@userId", userId);
    var deleted = await deleteUser.ExecuteNonQueryAsync();

    if (deleted == 0) return Results.NotFound(new { error = "User not found" });
    return Results.Json(new { success = true });
});

// DELETE /api/admin/users/cleanup - Delete all users without a valid email
app.MapDelete("/api/admin/users/cleanup", async () =>
{
    using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    // Get users with no email
    var userIds = new List<int>();
    using (var findCmd = new NpgsqlCommand(@"
        SELECT UserID FROM Users WHERE Email IS NULL OR TRIM(Email) = ''", conn))
    using (var reader = await findCmd.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync()) userIds.Add(reader.GetInt32(0));
    }

    if (userIds.Count == 0) return Results.Json(new { success = true, deleted = 0 });

    foreach (var userId in userIds)
    {
        using var deleteVotes = new NpgsqlCommand(@"
            DELETE FROM BuildVotes WHERE BuildID IN (SELECT SavedBuildID FROM SavedBuilds WHERE UserID = @userId)", conn);
        deleteVotes.Parameters.AddWithValue("@userId", userId);
        await deleteVotes.ExecuteNonQueryAsync();

        using var deleteBuilds = new NpgsqlCommand(@"DELETE FROM SavedBuilds WHERE UserID = @userId", conn);
        deleteBuilds.Parameters.AddWithValue("@userId", userId);
        await deleteBuilds.ExecuteNonQueryAsync();

        using var deleteTokens = new NpgsqlCommand(@"DELETE FROM PasswordResetTokens WHERE UserID = @userId", conn);
        deleteTokens.Parameters.AddWithValue("@userId", userId);
        await deleteTokens.ExecuteNonQueryAsync();

        using var unlinkOrders = new NpgsqlCommand(@"UPDATE Orders SET UserID = NULL WHERE UserID = @userId", conn);
        unlinkOrders.Parameters.AddWithValue("@userId", userId);
        await unlinkOrders.ExecuteNonQueryAsync();

        using var deleteUser = new NpgsqlCommand(@"DELETE FROM Users WHERE UserID = @userId", conn);
        deleteUser.Parameters.AddWithValue("@userId", userId);
        await deleteUser.ExecuteNonQueryAsync();
    }

    return Results.Json(new { success = true, deleted = userIds.Count });
});

// POST /api/auth/forgot-password
app.MapPost("/api/auth/forgot-password", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);

    var username = body.TryGetProperty("username", out var u) ? u.GetString()?.Trim() ?? "" : "";
    var email = body.TryGetProperty("email", out var e) ? e.GetString()?.Trim().ToLowerInvariant() ?? "" : "";

    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Username and email are required" });
        return;
    }

    try
    {
        using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        // Verify username + email match
        using var checkCmd = new NpgsqlCommand(@"
            SELECT UserID FROM Users WHERE Username = @username AND LOWER(Email) = @email", conn);
        checkCmd.Parameters.AddWithValue("@username", username);
        checkCmd.Parameters.AddWithValue("@email", email);

        var result = await checkCmd.ExecuteScalarAsync();
        if (result == null)
        {
            // Return generic message to avoid username/email enumeration
            await ctx.Response.WriteAsJsonAsync(new { success = true, message = "If an account with that username and email exists, a reset code has been generated." });
            return;
        }

        var userId = (int)result;

        // Invalidate any existing tokens for this user
        using var invalidateCmd = new NpgsqlCommand(@"
            UPDATE PasswordResetTokens SET Used = TRUE WHERE UserID = @userId AND Used = FALSE", conn);
        invalidateCmd.Parameters.AddWithValue("@userId", userId);
        await invalidateCmd.ExecuteNonQueryAsync();

        // Generate a 6-digit reset code
        var resetCode = RandomNumberGenerator.GetInt32(100000, 999999).ToString();

        // Store token with 15-minute expiry
        using var tokenCmd = new NpgsqlCommand(@"
            INSERT INTO PasswordResetTokens (UserID, Token, ExpiresAt)
            VALUES (@userId, @token, NOW() + INTERVAL '15 minutes')", conn);
        tokenCmd.Parameters.AddWithValue("@userId", userId);
        tokenCmd.Parameters.AddWithValue("@token", BCrypt.Net.BCrypt.HashPassword(resetCode));
        await tokenCmd.ExecuteNonQueryAsync();

        // Send reset code via Resend
        if (!string.IsNullOrEmpty(resendApiKey))
        {
            try
            {
                var emailPayload = JsonSerializer.Serialize(new
                {
                    from = emailFrom,
                    to = new[] { email },
                    subject = "HydraStack Password Reset Code",
                    html = $@"
                        <div style=""font-family: Arial, sans-serif; max-width: 480px; margin: 0 auto; padding: 2rem; background: #1E2128; color: #E8E0D5; border-radius: 16px;"">
                            <h2 style=""text-align: center; margin-bottom: 0.5rem;"">HydraStack</h2>
                            <p style=""text-align: center; color: #7788AA; font-size: 0.9rem;"">Password Reset Request</p>
                            <div style=""background: rgba(255,255,255,0.05); border: 1px solid #00D4AA; border-radius: 12px; padding: 1.5rem; text-align: center; margin: 1.5rem 0;"">
                                <div style=""font-size: 0.75rem; color: #7788AA; letter-spacing: 2px; margin-bottom: 0.5rem;"">YOUR RESET CODE</div>
                                <div style=""font-size: 2rem; font-weight: 700; color: #00D4AA; letter-spacing: 8px;"">{resetCode}</div>
                            </div>
                            <p style=""color: #7788AA; font-size: 0.85rem; text-align: center;"">This code expires in 15 minutes. If you didn't request this, you can ignore this email.</p>
                        </div>"
                });

                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
                request.Headers.Add("Authorization", $"Bearer {resendApiKey}");
                request.Content = new StringContent(emailPayload, System.Text.Encoding.UTF8, "application/json");

                var emailResponse = await httpClient.SendAsync(request);
                if (!emailResponse.IsSuccessStatusCode)
                {
                    var errorBody = await emailResponse.Content.ReadAsStringAsync();
                    Console.Error.WriteLine($"Resend email error: {emailResponse.StatusCode} - {errorBody}");
                }
            }
            catch (Exception emailEx)
            {
                Console.Error.WriteLine($"Failed to send reset email: {emailEx.Message}");
            }
        }
        else
        {
            Console.WriteLine($"[DEV] Reset code for {username}: {resetCode}");
        }

        await ctx.Response.WriteAsJsonAsync(new { success = true, message = "If an account with that username and email exists, a reset code has been sent to your email." });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Forgot password error: {ex.Message}");
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "Something went wrong. Please try again." });
    }
});

// POST /api/auth/reset-password
app.MapPost("/api/auth/reset-password", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);

    var username = body.TryGetProperty("username", out var u) ? u.GetString()?.Trim() ?? "" : "";
    var code = body.TryGetProperty("code", out var c) ? c.GetString()?.Trim() ?? "" : "";
    var newPassword = body.TryGetProperty("newPassword", out var p) ? p.GetString() ?? "" : "";

    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(newPassword))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "All fields are required" });
        return;
    }

    if (newPassword.Length < 6)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "New password must be at least 6 characters" });
        return;
    }

    try
    {
        using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        // Get user
        using var userCmd = new NpgsqlCommand(@"
            SELECT UserID FROM Users WHERE Username = @username", conn);
        userCmd.Parameters.AddWithValue("@username", username);
        var userResult = await userCmd.ExecuteScalarAsync();

        if (userResult == null)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "Invalid reset code" });
            return;
        }

        var userId = (int)userResult;

        // Find valid (unused, non-expired) tokens for this user
        using var tokenCmd = new NpgsqlCommand(@"
            SELECT TokenID, Token FROM PasswordResetTokens
            WHERE UserID = @userId AND Used = FALSE AND ExpiresAt > NOW()
            ORDER BY CreatedAt DESC LIMIT 5", conn);
        tokenCmd.Parameters.AddWithValue("@userId", userId);

        var validToken = false;
        var matchedTokenId = 0;

        using (var reader = await tokenCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var tokenId = reader.GetInt32(0);
                var tokenHash = reader.GetString(1);

                if (BCrypt.Net.BCrypt.Verify(code, tokenHash))
                {
                    validToken = true;
                    matchedTokenId = tokenId;
                    break;
                }
            }
        }

        if (!validToken)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "Invalid or expired reset code" });
            return;
        }

        // Mark token as used
        using var markUsedCmd = new NpgsqlCommand(@"
            UPDATE PasswordResetTokens SET Used = TRUE WHERE TokenID = @tokenId", conn);
        markUsedCmd.Parameters.AddWithValue("@tokenId", matchedTokenId);
        await markUsedCmd.ExecuteNonQueryAsync();

        // Update password
        var newHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        using var updateCmd = new NpgsqlCommand(@"
            UPDATE Users SET PasswordHash = @hash WHERE UserID = @userId", conn);
        updateCmd.Parameters.AddWithValue("@hash", newHash);
        updateCmd.Parameters.AddWithValue("@userId", userId);
        await updateCmd.ExecuteNonQueryAsync();

        await ctx.Response.WriteAsJsonAsync(new { success = true, message = "Password has been reset successfully" });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Reset password error: {ex.Message}");
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "Something went wrong. Please try again." });
    }
});

app.Run();
