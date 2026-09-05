# SAP2000 Error Handling & Return Codes

**Category:** Foundation  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Correctly interpret and handle CSI return codes, enabling robust error diagnosis and recovery.

**Rule:** Never silently ignore a CSI method return code.

---

## Return Code Convention

All CSI methods return `int` status:
- `0` = Success
- `nonzero` = Failure (specific error code)

Example:
```csharp
int ret = model.PointObj.AddPoint(0, 0, 0, "PT_1", "");
// ret = 0 → Success
// ret ≠ 0 → Failed (what went wrong?)
```

---

## Common Return Codes

| Code | Meaning | Action |
|------|---------|--------|
| 0 | Success | Continue |
| 1 | Geometry error / Invalid | Check coordinates, names, units |
| 2 | Model locked | Wait or force unlock |
| 3 | Insufficient data | Verify all required properties set |
| 4 | Object not found | Check object name/ID |
| 5 | Duplicate object | Name already exists |
| 6 | File I/O error | Check file path, permissions |

**Note:** CSI documentation doesn't always list all codes. Use context clues + SAP2000 log file.

---

## Project Convention: Explicit Error Reporting

All SAP method calls must include:
1. **Operation name** (what was attempted)
2. **Return code** (CSI error number)
3. **Context** (object, story, values, etc.)
4. **User message** (what to do next)

### Template

```csharp
int ret = model.PointObj.AddPoint(x, y, z, pointName, "");

if (ret != 0)
{
    throw new SAPException(
        operation: "PointObj.AddPoint",
        returnCode: ret,
        context: $"Point: {pointName}, X={x}, Y={y}, Z={z}",
        userMessage: "Failed to create point. Verify coordinates are in N-mm-C and name is unique.");
}
```

---

## Code Example: Custom Exception

```csharp
public class SAPException : Exception
{
    public string Operation { get; set; }
    public int ReturnCode { get; set; }
    public string Context { get; set; }
    public string UserMessage { get; set; }

    public SAPException(
        string operation,
        int returnCode,
        string context = "",
        string userMessage = "")
        : base(BuildMessage(operation, returnCode, context, userMessage))
    {
        Operation = operation;
        ReturnCode = returnCode;
        Context = context;
        UserMessage = userMessage;
    }

    private static string BuildMessage(
        string operation,
        int returnCode,
        string context,
        string userMessage)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SAP2000 API Error");
        sb.AppendLine($"  Operation: {operation}");
        sb.AppendLine($"  Return Code: {returnCode}");
        if (!string.IsNullOrEmpty(context))
            sb.AppendLine($"  Context: {context}");
        if (!string.IsNullOrEmpty(userMessage))
            sb.AppendLine($"  Message: {userMessage}");
        return sb.ToString();
    }
}
```

### Usage

```csharp
public void CreatePointOrThrow(
    cSapModel model,
    double x, double y, double z,
    string pointName)
{
    int ret = model.PointObj.AddPoint(x, y, z, pointName, "");

    if (ret != 0)
    {
        throw new SAPException(
            operation: "PointObj.AddPoint",
            returnCode: ret,
            context: $"Point='{pointName}', X={x} mm, Y={y} mm, Z={z} mm",
            userMessage: "Failed to create point. Verify coordinates are within model bounds, " +
                         "units are N-mm-C, and point name is unique (no duplicates)."
        );
    }
}
```

---

## Project Conventions

### Logging Failed Operations
Log full context for failed SAP operations (assists debugging).

```csharp
private readonly ILogger _logger;

public void CreatePointWithLogging(
    cSapModel model,
    double x, double y, double z,
    string pointName)
{
    _logger.LogInformation($"Creating point: {pointName} at ({x}, {y}, {z}) mm");

    int ret = model.PointObj.AddPoint(x, y, z, pointName, "");

    if (ret != 0)
    {
        _logger.LogError(
            $"Failed to create point '{pointName}'. " +
            $"Coordinates: X={x}, Y={y}, Z={z} mm. " +
            $"Return code: {ret}. " +
            $"This may indicate: duplicate name, invalid coordinates, or model lock.");

        throw new SAPException(
            operation: "PointObj.AddPoint",
            returnCode: ret,
            context: $"Point='{pointName}', ({x}, {y}, {z}) mm",
            userMessage: "See log for details. Check model lock, name uniqueness, and coordinate range."
        );
    }

    _logger.LogInformation($"Point '{pointName}' created successfully");
}
```

### Retry Logic (When Appropriate)

Some failures are transient (e.g., model lock). Others are permanent (duplicate name).

**Retry on:**
- Model lock (try again after waiting)
- Temporary resource conflict

**Do NOT retry on:**
- Duplicate name (fix the name, don't retry)
- Invalid geometry (fix coordinates, don't retry)
- Invalid units (fix units, don't retry)

```csharp
public void CreatePointWithRetry(
    cSapModel model,
    string pointName,
    double x, double y, double z,
    int maxRetries = 3)
{
    for (int attempt = 0; attempt < maxRetries; attempt++)
    {
        try
        {
            int ret = model.PointObj.AddPoint(x, y, z, pointName, "");
            if (ret == 0) return; // Success

            // Check if error is retryable
            if (ret == 2) // Model locked?
            {
                _logger.LogWarning(
                    $"Model locked on attempt {attempt + 1}/{maxRetries}. Waiting 100ms...");
                System.Threading.Thread.Sleep(100);
                continue;
            }

            // Permanent error, don't retry
            throw new SAPException(
                operation: "PointObj.AddPoint",
                returnCode: ret,
                context: $"Point='{pointName}'",
                userMessage: "Non-retryable error. Check point name uniqueness and coordinates.");
        }
        catch (Exception ex)
        {
            if (attempt == maxRetries - 1) throw;
            _logger.LogWarning($"Attempt {attempt + 1} failed: {ex.Message}");
        }
    }
}
```

---

## Validation Checklist

✓ All CSI method calls check return code  
✓ Return code 0 (success) handled explicitly  
✓ Non-zero return codes raise exceptions with context  
✓ Exception includes: operation name, return code, context, user message  
✓ Failed operations logged with full details  
✓ Retry logic only applied to transient errors  
✓ No silent failures or ignored return codes  
✓ User-facing error messages are actionable  

---

## Common Pitfalls

### ❌ Ignoring Return Code
```csharp
// WRONG: ret is ignored
int ret = model.PointObj.AddPoint(x, y, z, "PT_1", "");
// What if it failed? Code continues silently!
```

**Fix:** Always check and handle.

```csharp
int ret = model.PointObj.AddPoint(x, y, z, "PT_1", "");
if (ret != 0) throw new SAPException(...);
```

### ❌ Vague Error Message
```csharp
// WRONG: Doesn't help debugging
throw new Exception("SAP failed");
```

**Fix:** Include operation, code, and context.

```csharp
throw new SAPException(
    operation: "PointObj.AddPoint",
    returnCode: ret,
    context: $"Point '{pointName}' at ({x}, {y}, {z})",
    userMessage: "Failed to create point. Verify name is unique.");
```

### ❌ Retrying Non-Retryable Errors
```csharp
// WRONG: Retrying won't fix duplicate name
for (int i = 0; i < 3; i++)
{
    int ret = model.PointObj.AddPoint(x, y, z, "DUPLICATE_NAME", "");
    if (ret == 0) break; // Will never succeed!
}
```

**Fix:** Identify error type, retry only transient errors.

---

## External References

- **SAP2000 Help:** Search "Return codes", "Error handling"
- **CSI Forum:** https://wiki.csiamerica.com/
- **SAP2000 Log File:** Check `%APPDATA%\CSI\SAP2000\Logs\` for detailed error info

---

## Related Skills

- [Connection & Initialization](Connection.md) — Connection errors
- [Object Naming & Identification](ObjectNaming.md) — Duplicate name errors
