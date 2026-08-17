using System.Data.Common;
using System.Security.Claims;
using Api.Filters;
using Application.Interfaces.Repositories;
using ClosedXML.Excel;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Authorize]
[Route("api/checkin-reports")]
public sealed class CheckinReportsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHrRepository _hr;
    public CheckinReportsController(AppDbContext db, IHrRepository hr) { _db = db; _hr = hr; }

    [HttpGet]
    [RequirePermission("checkin_access")]
    public async Task<IActionResult> List([FromQuery] CheckinFilter filter, CancellationToken ct)
    {
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 10, 200);
        var visibleIds = await VisibleUserIds(ct);
        var total = await Count(filter, visibleIds, ct);
        var rows = await Rows(filter, visibleIds, (page - 1) * size, size, ct);
        return Ok(new { rows, total, page, page_size = size });
    }

    [HttpGet("options")]
    [RequirePermission("checkin_access")]
    public async Task<IActionResult> Options(CancellationToken ct)
    {
        var visibleIds = await VisibleUserIds(ct);
        var users = await _db.Users.AsNoTracking().Where(x => visibleIds.Contains(x.Id) && x.Active == "Y" && !x.IsDeleted)
            .OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.Mobile }).ToListAsync(ct);
        var divisions = await _db.Divisions.AsNoTracking().Where(x => x.Active == "Y").OrderBy(x => x.DivisionName).Select(x => new { x.Id, name = x.DivisionName }).ToListAsync(ct);
        var branches = await _db.Branches.AsNoTracking().Where(x => x.Active == "Y").OrderBy(x => x.BranchName).Select(x => new { x.Id, name = x.BranchName }).ToListAsync(ct);
        var designations = await _db.Designations.AsNoTracking().Where(x => x.Active == "Y").OrderBy(x => x.DesignationName).Select(x => new { x.Id, name = x.DesignationName }).ToListAsync(ct);
        return Ok(new { users, divisions, branches, designations });
    }

    [HttpGet("export")]
    [RequirePermission("checkin_download")]
    public async Task<IActionResult> Export([FromQuery] CheckinFilter filter, CancellationToken ct)
    {
        var rows = await Rows(filter, await VisibleUserIds(ct), null, null, ct);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Checkin Checkout");
        var headers = new[] { "ID", "Visit Date", "User ID", "Employee Code", "Employee Name", "Reporting Manager", "Designation", "Division", "Branch", "Checkin Time", "Checkout Time", "Spend Time", "Checkin Address", "Checkout Address", "Distance (KM)", "Customer ID", "Customer Type", "Customer Name", "Customer Mobile", "Beat Name", "City", "District", "Pincode", "Address", "Visit Type", "Visit Remark" };
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var values = new object?[] { r.Id, r.CheckinDate?.ToString("yyyy-MM-dd"), r.UserId, r.EmployeeCode, r.UserName, r.ReportingManager, r.Designation, r.Division, r.Branch, r.CheckinTime, r.CheckoutTime, r.TimeInterval, r.CheckinAddress, r.CheckoutAddress, r.Distance, r.CustomerId, r.CustomerType, r.CustomerName, FirstMobile(r.CustomerMobile), r.BeatName, r.City, r.District, r.Pincode, r.Address, r.VisitType, r.VisitRemark };
            for (var j = 0; j < values.Length; j++) sheet.Cell(i + 2, j + 1).Value = XLCellValue.FromObject(values[j]);
        }
        sheet.Row(1).Style.Font.Bold = true;
        sheet.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#1f4e78");
        sheet.Row(1).Style.Font.FontColor = XLColor.White;
        sheet.RangeUsed()!.SetAutoFilter();
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(8, 45);
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "checkin-checkout.xlsx");
    }

    private async Task<long> Count(CheckinFilter filter, IReadOnlyCollection<ulong> visibleIds, CancellationToken ct)
    {
        await using var command = CreateCommand("SELECT COUNT_BIG(*) FROM check_in ci LEFT JOIN users u ON u.id=ci.user_id LEFT JOIN customers c ON c.id=COALESCE(ci.entity_id,ci.customer_id) " + Where(filter, visibleIds, out var args));
        AddParameters(command, args); await Open(command, ct);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private async Task<List<CheckinRow>> Rows(CheckinFilter filter, IReadOnlyCollection<ulong> visibleIds, int? offset, int? size, CancellationToken ct)
    {
        const string select = @"SELECT CAST(ci.id AS bigint) id,ci.checkin_date,ci.checkin_time,ci.checkout_date,ci.checkout_time,ci.time_interval,
ci.checkin_latitude,ci.checkin_longitude,ci.checkin_address,ci.checkout_latitude,ci.checkout_longitude,ci.checkout_address,ci.distance,
CAST(ci.user_id AS bigint) user_id,u.name user_name,u.employee_codes,rm.name reporting_manager,dg.designation_name,dv.division_name,br.branch_name,
CAST(COALESCE(ci.entity_id,ci.customer_id) AS bigint) customer_id,c.name customer_name,c.mobile customer_mobile,ct.customertype_name customer_type,
c.latitude customer_latitude,c.longitude customer_longitude,
b.beat_name,city.city_name,district.district_name,COALESCE(pin.pincode,addr.zipcode,'') pincode,
LTRIM(RTRIM(CONCAT(COALESCE(addr.address1,''),CASE WHEN NULLIF(addr.address2,'') IS NULL THEN '' ELSE ' '+addr.address2 END))) customer_address,
vt.type_name visit_type,vr.description visit_remark,
COALESCE(ord.order_qty,0) order_qty,COALESCE(ord.order_value,0) order_value,COALESCE(ord.unique_sku,0) unique_sku,COALESCE(ord.unique_orders,0) unique_orders
FROM check_in ci
LEFT JOIN users u ON u.id=ci.user_id
LEFT JOIN users rm ON rm.id=u.reportingid
LEFT JOIN designations dg ON dg.id=u.designation_id
LEFT JOIN divisions dv ON dv.id=u.division_id
OUTER APPLY (SELECT TOP 1 brx.branch_name FROM branches brx
             WHERE brx.id=u.primary_branch_id OR ','+REPLACE(COALESCE(u.branch_id,''),' ','')+',' LIKE '%,'+CONVERT(varchar(30),brx.id)+',%'
             ORDER BY CASE WHEN brx.id=u.primary_branch_id THEN 0 ELSE 1 END,brx.id) br
LEFT JOIN customers c ON c.id=COALESCE(ci.entity_id,ci.customer_id)
LEFT JOIN customer_types ct ON ct.id=c.customertype
OUTER APPLY (SELECT TOP 1 a.city_id,a.district_id,a.pincode_id,a.zipcode,a.address1,a.address2 FROM addresses a WHERE a.customer_id=c.id AND a.deleted_at IS NULL ORDER BY a.id) addr
LEFT JOIN cities city ON city.id=addr.city_id
LEFT JOIN districts district ON district.id=addr.district_id
LEFT JOIN pincodes pin ON pin.id=addr.pincode_id
LEFT JOIN beat_schedules bs ON bs.id=ci.beatscheduleid
LEFT JOIN beats b ON b.id=bs.beat_id
OUTER APPLY (SELECT TOP 1 v.visit_type_id,v.description FROM visit_reports v WHERE v.checkin_id=ci.id AND v.deleted_at IS NULL ORDER BY v.id DESC) vr
LEFT JOIN visit_types vt ON vt.id=vr.visit_type_id
OUTER APPLY (SELECT SUM(o.total_qty) order_qty,SUM(o.grand_total) order_value,COUNT_BIG(DISTINCT o.id) unique_orders,
             (SELECT COUNT_BIG(DISTINCT od.product_id) FROM order_details od INNER JOIN orders ox ON ox.id=od.order_id
              WHERE ox.beatscheduleid=ci.beatscheduleid AND ox.deleted_at IS NULL) unique_sku
             FROM orders o WHERE o.beatscheduleid=ci.beatscheduleid AND o.deleted_at IS NULL) ord ";
        var sql = select + Where(filter, visibleIds, out var args) + " ORDER BY ci.checkin_date DESC,ci.checkin_time DESC,ci.id DESC";
        if (offset.HasValue && size.HasValue) { sql += " OFFSET @offset ROWS FETCH NEXT @size ROWS ONLY"; args.Add(("@offset", offset.Value)); args.Add(("@size", size.Value)); }
        await using var command = CreateCommand(sql); AddParameters(command, args); await Open(command, ct);
        var rows = new List<CheckinRow>(); await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var checkinLatitude = S(reader,"checkin_latitude");
            var checkinLongitude = S(reader,"checkin_longitude");
            var checkoutLatitude = S(reader,"checkout_latitude");
            var checkoutLongitude = S(reader,"checkout_longitude");
            var checkinAddress = S(reader,"checkin_address");
            var checkoutAddress = S(reader,"checkout_address");
            var distance = S(reader,"distance");
            if (string.IsNullOrWhiteSpace(distance))
                distance = DistanceKm(checkinLatitude, checkinLongitude, S(reader,"customer_latitude"), S(reader,"customer_longitude"));

            rows.Add(new CheckinRow {
            Id=L(reader,"id"),CheckinDate=D(reader,"checkin_date"),CheckinTime=S(reader,"checkin_time"),CheckoutDate=D(reader,"checkout_date"),CheckoutTime=S(reader,"checkout_time"),TimeInterval=S(reader,"time_interval"),
            CheckinLatitude=checkinLatitude,CheckinLongitude=checkinLongitude,CheckinAddress=checkinAddress,CheckoutLatitude=checkoutLatitude,CheckoutLongitude=checkoutLongitude,CheckoutAddress=checkoutAddress,Distance=distance,
            UserId=L(reader,"user_id"),UserName=S(reader,"user_name"),EmployeeCode=S(reader,"employee_codes"),ReportingManager=S(reader,"reporting_manager"),Designation=S(reader,"designation_name"),Division=S(reader,"division_name"),Branch=S(reader,"branch_name"),
            CustomerId=L(reader,"customer_id"),CustomerName=S(reader,"customer_name"),CustomerMobile=S(reader,"customer_mobile"),CustomerType=S(reader,"customer_type"),BeatName=S(reader,"beat_name"),City=S(reader,"city_name"),District=S(reader,"district_name"),Pincode=S(reader,"pincode"),Address=S(reader,"customer_address"),VisitType=S(reader,"visit_type"),VisitRemark=S(reader,"visit_remark"),OrderQty=L(reader,"order_qty"),OrderValue=M(reader,"order_value"),UniqueSku=L(reader,"unique_sku"),UniqueOrders=L(reader,"unique_orders")
            });
        }
        return rows;
    }

    // Laravel's distance() helper stores the straight-line distance between the
    // employee check-in position and the customer's registered GPS position.
    private static string DistanceKm(string latitude, string longitude, string customerLatitude, string customerLongitude)
    {
        if (!double.TryParse(latitude, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat1) ||
            !double.TryParse(longitude, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon1) ||
            !double.TryParse(customerLatitude, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat2) ||
            !double.TryParse(customerLongitude, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon2)) return "";
        if (lat1 is < -90 or > 90 || lat2 is < -90 or > 90 || lon1 is < -180 or > 180 || lon2 is < -180 or > 180) return "";
        const double radiusKm = 6371d;
        static double Radians(double degrees) => degrees * Math.PI / 180d;
        var dLat = Radians(lat2 - lat1);
        var dLon = Radians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return (radiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a))).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool ValidCoordinate(string latitude, string longitude) =>
        double.TryParse(latitude, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat) &&
        double.TryParse(longitude, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon) &&
        lat is >= -90 and <= 90 && lon is >= -180 and <= 180;

    private string Where(CheckinFilter f, IReadOnlyCollection<ulong> visibleIds, out List<(string,object)> args)
    {
        args=[]; var where=new List<string>{"ci.deleted_at IS NULL"};
        if (visibleIds.Count == 0) where.Add("1=0");
        else
        {
            var userParams = visibleIds.Select((id, index) => (Name: $"@visible{index}", Value: (object)(decimal)id)).ToArray();
            where.Add($"ci.user_id IN ({string.Join(',', userParams.Select(x => x.Name))})");
            args.AddRange(userParams);
        }
        if(f.StartDate.HasValue){where.Add("ci.checkin_date>=@start");args.Add(("@start",f.StartDate.Value.Date));}
        if(f.EndDate.HasValue){where.Add("ci.checkin_date<=@end");args.Add(("@end",f.EndDate.Value.Date));}
        if(f.UserId.HasValue){where.Add("ci.user_id=@user");args.Add(("@user",(decimal)f.UserId.Value));}
        if(f.DivisionId.HasValue){where.Add("u.division_id=@division");args.Add(("@division",(decimal)f.DivisionId.Value));}
        if(f.BranchId.HasValue){where.Add("(u.primary_branch_id=@branch OR ','+REPLACE(COALESCE(u.branch_id,''),' ','')+',' LIKE @branchCsv)");args.Add(("@branch",(decimal)f.BranchId.Value));args.Add(("@branchCsv",$"%,{f.BranchId.Value},%"));}
        if(f.DesignationIds is { Count: > 0 })
        {
            var designationParams=f.DesignationIds.Distinct().Select((id,index)=>(Name:$"@designation{index}",Value:(object)(decimal)id)).ToArray();
            where.Add($"u.designation_id IN ({string.Join(',',designationParams.Select(x=>x.Name))})");args.AddRange(designationParams);
        }
        if(!string.IsNullOrWhiteSpace(f.Search)){where.Add("(u.name LIKE @search OR c.name LIKE @search OR c.mobile LIKE @search OR ci.checkin_address LIKE @search OR b.beat_name LIKE @search)");args.Add(("@search","%"+f.Search.Trim()+"%"));}
        return " WHERE "+string.Join(" AND ",where);
    }
    private DbCommand CreateCommand(string sql){var c=_db.Database.GetDbConnection().CreateCommand();c.CommandText=sql;c.CommandTimeout=120;return c;}
    private static void AddParameters(DbCommand c,IEnumerable<(string Name,object Value)> args){foreach(var a in args){var p=c.CreateParameter();p.ParameterName=a.Name;p.Value=a.Value;c.Parameters.Add(p);}}
    private static async Task Open(DbCommand c,CancellationToken ct){if(c.Connection!.State!=System.Data.ConnectionState.Open)await c.Connection.OpenAsync(ct);}
    private static string S(DbDataReader r,string n){var v=r[n];if(v is DBNull)return "";if(v is TimeSpan t)return t.ToString(@"hh\:mm\:ss");return Convert.ToString(v)??"";}
    private static long L(DbDataReader r,string n)=>r[n] is DBNull?0:Convert.ToInt64(r[n]);
    private static decimal M(DbDataReader r,string n)=>r[n] is DBNull?0:Convert.ToDecimal(r[n]);
    private static DateTime? D(DbDataReader r,string n)=>r[n] is DBNull?null:Convert.ToDateTime(r[n]);
    private static string FirstMobile(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
    private ulong CurrentUserId() => ulong.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : throw new UnauthorizedAccessException("Authenticated user id is missing.");
    private async Task<IReadOnlyCollection<ulong>> VisibleUserIds(CancellationToken ct) =>
        (await _hr.GetVisibleUserIdsAsync(CurrentUserId(), ct)).Distinct().ToArray();

    public sealed class CheckinFilter
    {
        public int Page { get; set; } = 1;
        [FromQuery(Name = "page_size")] public int PageSize { get; set; } = 25;
        public string? Search { get; set; }
        [FromQuery(Name = "start_date")] public DateTime? StartDate { get; set; }
        [FromQuery(Name = "end_date")] public DateTime? EndDate { get; set; }
        [FromQuery(Name = "user_id")] public ulong? UserId { get; set; }
        [FromQuery(Name = "division_id")] public ulong? DivisionId { get; set; }
        [FromQuery(Name = "branch_id")] public ulong? BranchId { get; set; }
        [FromQuery(Name = "designation_id")] public List<ulong> DesignationIds { get; set; } = [];
    }
    public sealed class CheckinRow { public long Id{get;set;} public DateTime? CheckinDate{get;set;} public string CheckinTime{get;set;}=""; public DateTime? CheckoutDate{get;set;} public string CheckoutTime{get;set;}=""; public string TimeInterval{get;set;}=""; public string CheckinLatitude{get;set;}=""; public string CheckinLongitude{get;set;}=""; public string CheckinAddress{get;set;}=""; public string CheckoutLatitude{get;set;}=""; public string CheckoutLongitude{get;set;}=""; public string CheckoutAddress{get;set;}=""; public string Distance{get;set;}=""; public long UserId{get;set;} public string UserName{get;set;}=""; public string EmployeeCode{get;set;}=""; public string ReportingManager{get;set;}=""; public string Designation{get;set;}=""; public string Division{get;set;}=""; public string Branch{get;set;}=""; public long CustomerId{get;set;} public string CustomerName{get;set;}=""; public string CustomerMobile{get;set;}=""; public string CustomerType{get;set;}=""; public string BeatName{get;set;}=""; public string City{get;set;}=""; public string District{get;set;}=""; public string Pincode{get;set;}=""; public string Address{get;set;}=""; public string VisitType{get;set;}=""; public string VisitRemark{get;set;}=""; public long OrderQty{get;set;} public decimal OrderValue{get;set;} public long UniqueSku{get;set;} public long UniqueOrders{get;set;} }
}
