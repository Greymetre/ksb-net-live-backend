using System.Security.Claims;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Api.Controllers;

[ApiController, Authorize, Route("api/activities")]
public sealed class PromotionalActivitiesController : ControllerBase
{
    private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase) { "nukkad", "retailer", "farmer", "influencer" };
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly IHrRepository _hr;
    public PromotionalActivitiesController(AppDbContext db, IWebHostEnvironment environment,IConfiguration configuration, IHrRepository hr) { _db = db; _environment = environment; _configuration = configuration; _hr = hr; }

    [HttpGet("config/{type}")]
    public async Task<IActionResult> Config(string type, CancellationToken ct)
    {
        if (!Types.Contains(type)) return NotFound(new { status = "error", message = "Invalid activity type." });
        type = type.ToLowerInvariant();
        var dealerShare = type is "retailer" or "influencer";
        var userId = CurrentUserId();
        var profile = await _db.Users.AsNoTracking().Where(x => x.Id == userId).Select(x => new { x.Name, x.PrimaryBranchId, x.BranchId, x.BranchShow, x.ReportingId }).FirstOrDefaultAsync(ct);
        var assignedBranchIds = (profile?.BranchId ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => ulong.TryParse(x.Trim(), out var id) ? (ulong?)id : null).Where(x => x.HasValue).Select(x => x!.Value).ToList();
        var primaryBranchId = profile?.PrimaryBranchId ?? assignedBranchIds.FirstOrDefault();
        var branchNames = assignedBranchIds.Count == 0 && primaryBranchId > 0
            ? await _db.Branches.AsNoTracking().Where(x => x.Id == primaryBranchId).Select(x => x.BranchName).ToListAsync(ct)
            : await _db.Branches.AsNoTracking().Where(x => assignedBranchIds.Contains(x.Id)).OrderBy(x => x.BranchName).Select(x => x.BranchName).ToListAsync(ct);
        var branchName = string.Join(", ", branchNames);
        var managerName = profile?.ReportingId == null ? null : await _db.Users.AsNoTracking().Where(x => x.Id == profile.ReportingId).Select(x => x.Name).FirstOrDefaultAsync(ct);
        const ulong asrDesignationId = 3;
        const ulong dsrDesignationId = 6;
        var visibleUserIds = await VisibleActivityUserIds(userId, ct);
        var asrDsrUsers = await _db.Users.AsNoTracking()
            .Where(x => visibleUserIds.Contains(x.Id) && (x.DesignationId == asrDesignationId || x.DesignationId == dsrDesignationId))
            .OrderBy(x => x.Name)
            .Select(x => new { id = x.Id, name = x.Name, designation_id = x.DesignationId })
            .ToListAsync(ct);
        var divisionId = await _db.Users.AsNoTracking().Where(x=>x.Id==userId).Select(x=>x.DivisionId).FirstOrDefaultAsync(ct);
        var zoneName = divisionId.HasValue ? await _db.Divisions.AsNoTracking().Where(x=>x.Id==divisionId.Value).Select(x=>x.DivisionName).FirstOrDefaultAsync(ct) ?? "" : "";
        var fields = ActivityConfig.Build(type, branchName, zoneName, managerName ?? profile?.Name, profile?.Name, primaryBranchId == 0 ? null : primaryBranchId, profile?.ReportingId ?? userId);
        return Ok(new { status="success", data=new {
            type, current_user_id = userId, title = type switch { "nukkad"=>"Nukkad Meet", "retailer"=>"Retailer Meet", "farmer"=>"Farmer Meet / Field Demo", _=>"Influencer Meet" },
            dealer_label = type == "nukkad" ? "Retailer Name" : "Sub Dealer Name", hotel = type is "retailer" or "influencer",
            shop_mode = type == "retailer", profession = type != "retailer", asr_auto = type == "nukkad", dealer_share = dealerShare,
            photo_limit = type == "retailer" ? 10 : 5,
            photo_minimum = 0,
            activity_names = type == "farmer" ? new[]{"Farmer Meet","Field Demo"} : Array.Empty<string>(),
            asr_dsr_users = type == "nukkad" ? Array.Empty<object>() : asrDsrUsers.Cast<object>().ToArray(),
            expense_types = type switch { "nukkad"=>new[]{"food","gift"}, "farmer"=>new[]{"food","gift","other1","other2"}, _=>new[]{"food","gift","hotel","av","other"} },
            fields
        }});
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page=1, [FromQuery] int per_page=20, [FromQuery] string? activity_type=null, [FromQuery] string? date_range=null, [FromQuery] ulong? user=null, [FromQuery] DateTime? start_date=null, [FromQuery] DateTime? end_date=null, CancellationToken ct=default)
    {
        page=Math.Max(1,page); per_page=Math.Clamp(per_page,1,100); var current=CurrentUserId();
        var activityUserId=checked((long)(user??current));
        var visibleUserIds=await VisibleActivityUserIds(current,ct);
        var visibleActivityUserIds=visibleUserIds.Select(x=>checked((long)x)).ToArray();
        var q=_db.PromotionalActivities.AsNoTracking().Where(x=>x.DeletedAt==null);
        q=q.Where(x=>visibleActivityUserIds.Contains(x.CreatedById)||visibleActivityUserIds.Contains(x.UserId));
        if(user.HasValue&&visibleUserIds.Contains(user.Value))q=q.Where(x=>x.CreatedById==activityUserId||x.UserId==activityUserId);
        var today=DateTime.UtcNow.Date;
        if (date_range=="today") q=q.Where(x=>x.ActivityDate==today);
        else if(date_range=="year") q=q.Where(x=>x.ActivityDate.Year==today.Year);
        else if(date_range=="month") q=q.Where(x=>x.ActivityDate.Year==today.Year&&x.ActivityDate.Month==today.Month);
        else if(date_range=="quarter")
        {
            var quarterStartMonth=((today.Month-1)/3)*3+1;
            var quarterStart=new DateTime(today.Year,quarterStartMonth,1);
            var quarterEnd=quarterStart.AddMonths(3);
            q=q.Where(x=>x.ActivityDate>=quarterStart&&x.ActivityDate<quarterEnd);
        }
        else if(date_range=="custom")
        {
            if(start_date.HasValue)q=q.Where(x=>x.ActivityDate>=start_date.Value.Date);
            if(end_date.HasValue)q=q.Where(x=>x.ActivityDate<=end_date.Value.Date);
        }
        var typeTotals=await q.GroupBy(x=>x.ActivityType).Select(group=>new{type=group.Key,count=group.Count()}).ToListAsync(ct);
        var counts=new {
            all=typeTotals.Sum(x=>x.count),
            retailer=typeTotals.FirstOrDefault(x=>x.type=="retailer")?.count??0,
            nukkad=typeTotals.FirstOrDefault(x=>x.type=="nukkad")?.count??0,
            farmer=typeTotals.FirstOrDefault(x=>x.type=="farmer")?.count??0,
            influencer=typeTotals.FirstOrDefault(x=>x.type=="influencer")?.count??0
        };
        if (!string.IsNullOrWhiteSpace(activity_type) && Types.Contains(activity_type)) q=q.Where(x=>x.ActivityType==activity_type.ToLower());
        var total=await q.CountAsync(ct); var rows=await q.OrderByDescending(x=>x.ActivityDate).ThenByDescending(x=>x.Id).Skip((page-1)*per_page).Take(per_page)
            .Select(x=>new { x.Id,x.ActivityType,x.ActivityName,x.ActivityDate,x.DistributorName,x.Zone,x.TotalExpense,x.GiftCount,x.Status, participant_count=x.Participants.Count, branch=x.BranchId }).ToListAsync(ct);
        return Ok(new { status="success", data=rows, meta=new { current_page=page,per_page,total,last_page=Math.Max(1,(int)Math.Ceiling(total/(double)per_page)),counts } });
    }

    [HttpGet("filters")]
    public async Task<IActionResult> Filters(CancellationToken ct)
    {
        var current=CurrentUserId();
        var visibleUserIds=await VisibleActivityUserIds(current,ct);
        var usersQuery=_db.Users.AsNoTracking().Where(x=>x.Active=="Y"&&!x.IsDeleted&&(x.DesignationId==3||x.DesignationId==6));
        usersQuery=usersQuery.Where(x=>visibleUserIds.Contains(x.Id));
        var users=await usersQuery.OrderBy(x=>x.Name).Select(x=>new{x.Id,x.Name,x.DesignationId}).ToListAsync(ct);
        return Ok(new{status="success",data=new{users}});
    }

    [HttpGet("dashboard-summary")]
    public async Task<IActionResult> DashboardSummary([FromQuery] string date_range="today", CancellationToken ct=default)
    {
        var current=CurrentUserId();
        var visibleUserIds=await VisibleActivityUserIds(current,ct);
        var today=DateTime.UtcNow.Date;
        var startDate=date_range.ToLowerInvariant() switch
        {
            "month" => new DateTime(today.Year,today.Month,1),
            "year" => new DateTime(today.Year,1,1),
            _ => today
        };
        var until=today.AddDays(1);
        var users=_db.Users.AsNoTracking().Where(x=>visibleUserIds.Contains(x.Id)&&(x.DesignationId==3||x.DesignationId==6));
        var query=from activity in _db.PromotionalActivities.AsNoTracking()
                  join employee in users on (ulong)activity.UserId equals employee.Id
                  where activity.DeletedAt==null&&activity.ActivityDate>=startDate&&activity.ActivityDate<until
                  select new { activity.ActivityType, employee.DesignationId };
        var rows=await query.GroupBy(x=>new{x.DesignationId,x.ActivityType})
            .Select(group=>new{group.Key.DesignationId,group.Key.ActivityType,Count=group.Count()}).ToListAsync(ct);

        object Counts(ulong designationId)
        {
            int Count(string type)=>rows.FirstOrDefault(x=>x.DesignationId==designationId&&x.ActivityType==type)?.Count??0;
            return new{retailer=Count("retailer"),nukkad=Count("nukkad"),farmer=Count("farmer"),influencer=Count("influencer")};
        }
        return Ok(new{status="success",data=new{asr=Counts(3),dsr=Counts(6)}});
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        var item=await _db.PromotionalActivities.AsNoTracking().Include(x=>x.Participants).Include(x=>x.Expenses).Include(x=>x.Photos).FirstOrDefaultAsync(x=>x.Id==id&&x.DeletedAt==null,ct);
        return item==null ? NotFound(new {status="error",message="Activity not found."}) : Ok(new {status="success",data=item});
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ActivityRequest request, CancellationToken ct)
    {
        var error=ValidateDraft(request); if(error!=null) return UnprocessableEntity(new {status="error",message=error});
        var entity=Map(request,new PromotionalActivity{CreatedById=checked((long)CurrentUserId()),CreatedAt=DateTime.UtcNow});
        entity.ActivityCode=await NextActivityCodeAsync(entity.ActivityType,entity.ActivityDate,ct);
        _db.PromotionalActivities.Add(entity); await _db.SaveChangesAsync(ct);
        return Ok(new {status="success",message="Activity draft saved.",data=new {entity.Id,entity.ActivityCode,entity.Status}});
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id,[FromBody] ActivityRequest request,CancellationToken ct)
    {
        var currentUserId=checked((long)CurrentUserId());
        var entity=await _db.PromotionalActivities.Include(x=>x.Participants).Include(x=>x.Expenses).Include(x=>x.Photos).FirstOrDefaultAsync(x=>x.Id==id&&(x.CreatedById==currentUserId||x.UserId==currentUserId)&&x.DeletedAt==null,ct);
        if(entity==null)return NotFound(new{status="error",message="Activity not found."}); if(entity.Status=="submitted")return Conflict(new{status="error",message="Submitted activity cannot be edited."});
        var error=ValidateDraft(request); if(error!=null)return UnprocessableEntity(new{status="error",message=error});
        _db.RemoveRange(entity.Participants);_db.RemoveRange(entity.Expenses);_db.RemoveRange(entity.Photos); Map(request,entity);entity.UpdatedAt=DateTime.UtcNow;await _db.SaveChangesAsync(ct);
        return Ok(new{status="success",message="Activity draft updated.",data=new{entity.Id,entity.ActivityCode,entity.Status}});
    }

    [HttpPost("{id:long}/submit")]
    public async Task<IActionResult> Submit(long id,CancellationToken ct)
    {
        var currentUserId=checked((long)CurrentUserId());
        var entity=await _db.PromotionalActivities.Include(x=>x.Participants).Include(x=>x.Expenses).Include(x=>x.Photos).FirstOrDefaultAsync(x=>x.Id==id&&(x.CreatedById==currentUserId||x.UserId==currentUserId)&&x.DeletedAt==null,ct);
        if(entity==null)return NotFound(new{status="error",message="Activity not found."}); if(entity.Status=="submitted")return Ok(new{status="success",message="Activity already submitted."});
        var submitError=ValidateSubmit(entity);if(submitError!=null)return UnprocessableEntity(new{status="error",message=submitError});
        entity.Status="submitted";entity.UpdatedAt=DateTime.UtcNow;await _db.SaveChangesAsync(ct);return Ok(new{status="success",message="Activity submitted successfully."});
    }

    [HttpPost("upload")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> Upload([FromForm] IFormFile file,CancellationToken ct)
    {
        if(file.Length==0||file.Length>20_000_000)return BadRequest(new{status="error",message="Invalid file."});
        var ext=Path.GetExtension(file.FileName).ToLowerInvariant();
        if(ext is not (".jpg" or ".jpeg" or ".png" or ".webp"))return UnprocessableEntity(new{status="error",message="Only JPG, PNG and WebP activity photos are allowed."});
        var name=$"{Guid.NewGuid():N}{ext}";var legacyRoot=_configuration["FileUploads:LegacyFilesRoot"];
        var directory=!string.IsNullOrWhiteSpace(legacyRoot)?Path.Combine(legacyRoot,"uploads","promotional-activities"):Path.Combine(_environment.WebRootPath??Path.Combine(_environment.ContentRootPath,"wwwroot"),"uploads","promotional-activities");
        Directory.CreateDirectory(directory);await using var stream=System.IO.File.Create(Path.Combine(directory,name));await file.CopyToAsync(stream,ct);
        return Ok(new{status="success",data=new{url=$"/uploads/promotional-activities/{name}"}});
    }

    private static readonly IReadOnlyDictionary<string,string> ActivityCodePrefixes=new Dictionary<string,string>
    { ["retailer"]="RTL", ["nukkad"]="NKD", ["farmer"]="FRM", ["influencer"]="INF" };

    /// <summary>
    /// Builds the readable activity id, for example ACT-RTL-2608-0042. The serial runs
    /// per activity type and month, and existing codes are read back so a gap or a
    /// deleted row never causes a duplicate.
    /// </summary>
    private async Task<string> NextActivityCodeAsync(string activityType,DateTime activityDate,CancellationToken ct)
    {
        var prefix=$"ACT-{ActivityCodePrefixes.GetValueOrDefault(activityType,"GEN")}-{activityDate:yyMM}-";
        var used=await _db.PromotionalActivities.IgnoreQueryFilters().AsNoTracking()
            .Where(x=>x.ActivityCode!=null&&x.ActivityCode.StartsWith(prefix))
            .Select(x=>x.ActivityCode!)
            .ToListAsync(ct);
        var highest=used
            .Select(x=>int.TryParse(x[prefix.Length..],out var serial)?serial:0)
            .DefaultIfEmpty(0)
            .Max();
        return $"{prefix}{highest+1:0000}";
    }

    private PromotionalActivity Map(ActivityRequest r,PromotionalActivity e)
    {
        e.ActivityType=(r.ActivityType??"").ToLower();e.ActivityName=(r.ActivityName??"").Trim();e.ActivityDate=r.ActivityDate.Date;e.UserId=checked((long)(e.ActivityType=="nukkad"?CurrentUserId():(r.UserId??CurrentUserId())));e.BranchId=r.BranchId.HasValue?checked((long)r.BranchId.Value):null;e.Zone=r.Zone;e.ReportingManagerId=r.ReportingManagerId.HasValue?checked((long)r.ReportingManagerId.Value):null;e.DistributorId=r.DistributorId.HasValue?checked((long)r.DistributorId.Value):null;e.DistributorName=r.DistributorName;e.DealerName=r.DealerName;e.HotelName=r.HotelName;e.LocationLat=r.LocationLat;e.LocationLng=r.LocationLng;e.LocationText=r.LocationText;e.GiftCount=Math.Max(0,r.GiftCount);e.Feedback=r.Feedback;e.Status="draft";
        e.Participants=r.Participants.Select(x=>new PromotionalActivityParticipant{Name=x.Name,ShopName=x.ShopName,ProprietorName=x.ProprietorName,ParticipantType=x.ParticipantType,Profession=x.Profession,Mobile=x.Mobile,GiftName=x.GiftName,Remarks=x.Remarks,IsInfluencer=x.IsInfluencer,SocialType=x.SocialType,SocialLink=x.SocialLink,CreatedAt=DateTime.UtcNow}).ToList();
        e.Expenses=r.Expenses.Select(x=>new PromotionalActivityExpense{ExpenseType=x.ExpenseType,TotalAmount=Math.Max(0,x.TotalAmount),DealerShareAmount=Math.Max(0,x.DealerShareAmount),DealerSharePct=x.TotalAmount<=0?0:Math.Round(x.DealerShareAmount/x.TotalAmount*100,2),Remarks=x.Remarks,InvoiceUrl=x.InvoiceUrl,CreatedAt=DateTime.UtcNow}).ToList();
        e.Photos=r.Photos.Select(x=>new PromotionalActivityPhoto{PhotoUrl=x.PhotoUrl,Latitude=x.Latitude,Longitude=x.Longitude,TakenAt=x.TakenAt,CreatedAt=DateTime.UtcNow}).ToList();e.TotalExpense=e.Expenses.Sum(x=>x.TotalAmount);e.DealerShareAmount=e.Expenses.Sum(x=>x.DealerShareAmount);return e;
    }
    private static string? ValidateDraft(ActivityRequest r)
    { if(!Types.Contains(r.ActivityType??""))return "Select a valid activity type.";var max=r.ActivityType.Equals("retailer",StringComparison.OrdinalIgnoreCase)?10:5;if((r.Photos?.Count??0)>max)return $"Maximum {max} photos are allowed.";if(r.LocationLat is <-90 or >90||r.LocationLng is <-180 or >180)return "Invalid activity latitude or longitude.";return null; }
    internal static string? ValidateSubmit(PromotionalActivity e)
    {
        var retailer=e.ActivityType=="retailer";var hotel=retailer||e.ActivityType=="influencer";var photoCount=retailer?10:5;
        if(e.ActivityDate==default)return "Activity Date is required.";if(e.ActivityDate>DateTime.UtcNow.Date)return "Activity Date cannot be a future date.";if(string.IsNullOrWhiteSpace(e.ActivityName))return "Activity Name is required.";if(e.BranchId==null||string.IsNullOrWhiteSpace(e.Zone))return "Branch & Zone are required.";if(e.ReportingManagerId==null)return "Reporting Manager is required.";if(e.UserId==0)return "ASR / DSR Name is required.";if(e.DistributorId==null)return "Distributor Code & Name is required.";if(hotel&&string.IsNullOrWhiteSpace(e.HotelName))return "Hotel Name is required.";if(string.IsNullOrWhiteSpace(e.LocationText))return "Activity Location is required.";
        if(e.Participants.Count==0)return "Add at least one participant.";foreach(var p in e.Participants){if(retailer&&string.IsNullOrWhiteSpace(p.ShopName))return "Retailer Shop Name is required.";if(!retailer&&string.IsNullOrWhiteSpace(p.Name))return "Participant Name is required.";if(!string.IsNullOrWhiteSpace(p.Mobile)&&(!p.Mobile.All(char.IsDigit)||p.Mobile.Length!=10))return "Participant mobile must contain exactly 10 digits.";if(p.IsInfluencer&&string.IsNullOrWhiteSpace(p.SocialType))return "Social Media Type is required for an influencer.";}
        var expected=e.ActivityType switch{"nukkad"=>new[]{"food","gift"},"farmer"=>new[]{"food","gift","other1","other2"},_=>new[]{"food","gift","hotel","av","other"}};foreach(var key in expected){var x=e.Expenses.FirstOrDefault(v=>v.ExpenseType==key);if(x==null)return $"{key} expense is required.";if(x.TotalAmount<0)return "Expense amount cannot be negative.";if((key is "hotel" or "other" or "other1" or "other2")&&string.IsNullOrWhiteSpace(x.Remarks))return $"Remarks are required for {key} expense.";}
        if(e.Photos.Count>photoCount)return $"Maximum {photoCount} activity photos are allowed.";if(e.Photos.Any(x=>x.Latitude is <-90 or >90||x.Longitude is <-180 or >180))return "A photo contains invalid geo coordinates.";return null;
    }
    private ulong CurrentUserId()=>ulong.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier),out var id)?id:throw new InvalidOperationException("Unauthenticated.");

    private async Task<HashSet<ulong>> VisibleActivityUserIds(ulong currentUserId,CancellationToken ct)
        => (await _hr.GetVisibleUserIdsAsync(currentUserId,ct)).ToHashSet();
}

public sealed record ActivityRequest(string ActivityType,string ActivityName,DateTime ActivityDate,ulong? UserId,ulong? BranchId,string? Zone,ulong? ReportingManagerId,ulong? DistributorId,string? DistributorName,string? DealerName,string? HotelName,decimal? LocationLat,decimal? LocationLng,string? LocationText,int GiftCount,string? Feedback,List<ActivityParticipantRequest> Participants,List<ActivityExpenseRequest> Expenses,List<ActivityPhotoRequest> Photos);
public sealed record ActivityParticipantRequest(string? Name,string? ShopName,string? ProprietorName,string? ParticipantType,string? Profession,string? Mobile,string? GiftName,string? Remarks,bool IsInfluencer,string? SocialType,string? SocialLink);
public sealed record ActivityExpenseRequest(string ExpenseType,decimal TotalAmount,decimal DealerShareAmount,string? Remarks,string? InvoiceUrl);
public sealed record ActivityPhotoRequest(string PhotoUrl,decimal Latitude,decimal Longitude,DateTime? TakenAt);

internal static class ActivityConfig
{
    public static IReadOnlyList<ActivityFieldConfig> Build(string type,string? branch,string? zone,string? manager,string? user,ulong? branchId,ulong? managerId)
    {
        var retailer=type=="retailer";var farmer=type=="farmer";var hotel=retailer||type=="influencer";var asrAuto=type=="nukkad";
        return new List<ActivityFieldConfig>{
            F("activityDate","Activity Date","manual","date_picker",true,true,DateTime.UtcNow.ToString("yyyy-MM-dd")),
            F("activityName","Activity Name",farmer?"manual":"auto",farmer?"dropdown":"readonly",true,true,farmer?"Farmer Meet":type switch{"nukkad"=>"Nukkad Meet","retailer"=>"Retailer Meet",_=>"Influencer Meet"},farmer?["Farmer Meet","Field Demo"]:[]),
            F("branchZone","Branch & Zone","auto","readonly",true,true,string.Join(" · ",new[]{branch,zone}.Where(x=>!string.IsNullOrWhiteSpace(x))),metadata:new(){["branch_id"]=branchId}),
            F("reportingManager","Reporting Manager","auto","readonly",true,true,manager,metadata:new(){["reporting_manager_id"]=managerId}),
            F("userId","ASR / DSR Name",asrAuto?"auto":"manual",asrAuto?"readonly":"dropdown",true,true,asrAuto?user:null),
            F("distributorId","Distributor Code & Name","manual","search_select",true,true,null),
            F("dealerName",type=="nukkad"?"Retailer Name":"Sub Dealer","manual","dropdown",false,true,null),
            F("hotelName","Hotel Name","manual","text",hotel,hotel,null),
            F("locationText","Activity Location","manual","text",true,true,null),
            F("participantName",retailer?"Retailer Shop Name":"Participant Name","manual","text",true,true,null),
            F("proprietorName","Proprietor Name","manual","text",false,retailer,null),
            F("participantType","Participant Type","manual","dropdown",false,true,null,["Retailer","Plumber","Mechanic","Other"]),
            F("profession","Profession","manual","dropdown",false,!retailer,farmer?"Farmer":null,["Mechanic","Plumber","Electrician","Borer","Farmer","Other"]),
            F("mobile","Mobile","manual","tel",false,true,null),F("giftName","Gift Name","manual","dropdown",false,true,null),F("remarks","Remarks","manual","text",false,true,null),
            F("isInfluencer","Social Media Influencer?","manual","toggle",true,true,"false",["No","Yes"]),F("socialType","Social Media Type","manual","dropdown",true,true,null,["Instagram","Facebook","LinkedIn","YouTube"],new(){["visible_when"]="isInfluencer=true"}),F("socialLink","Profile Link","manual","url",false,true,null,metadata:new(){["visible_when"]="isInfluencer=true"}),
            F("giftCount","Gift Count (Total)","manual","stepper",true,true,"0"),F("totalParticipants","Total Participants","derived","readonly",false,true,"0"),F("giftQty","Gift Qty (Nos)","derived","readonly",false,true,"0"),
            F("totalExpense","Total Expense","derived","readonly",false,true,"0"),F("photos","Activity Photos","manual","camera_upload",false,true,null,metadata:new(){["minimum"]=0,["maximum"]=(retailer?10:5)}),F("feedback","Feedback from Participants","manual","textarea",false,true,null)
        };
    }
    private static ActivityFieldConfig F(string key,string label,string source,string input,bool required,bool visible,object? value,IReadOnlyList<string>? options=null,Dictionary<string,object?>? metadata=null)=>new(key,label,source,input,required,visible,value,options??Array.Empty<string>(),metadata??new());
}
internal sealed record ActivityFieldConfig(string Key,string Label,string Source,string InputType,bool Required,bool Visible,object? DefaultValue,IReadOnlyList<string> Options,IReadOnlyDictionary<string,object?> Metadata);

[ApiController, Authorize, Route("api/distributors")]
public sealed class ActivityDistributorsController : ControllerBase
{
    private readonly AppDbContext _db; public ActivityDistributorsController(AppDbContext db)=>_db=db;
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery]string? q,[FromQuery]ulong? user,CancellationToken ct)
    {
        if(!user.HasValue||user.Value==0)return BadRequest(new{status="error",message="Please select ASR / DSR first."});
        var assignedCustomerIds=new List<ulong>();var connection=_db.Database.GetDbConnection();if(connection.State!=System.Data.ConnectionState.Open)await connection.OpenAsync(ct);await using(var command=connection.CreateCommand()){command.CommandText="SELECT DISTINCT customer_id FROM employee_details WHERE user_id=@user_id AND deleted_at IS NULL AND active='Y' AND customer_id IS NOT NULL";var parameter=command.CreateParameter();parameter.ParameterName="@user_id";parameter.Value=Convert.ToDecimal(user.Value);command.Parameters.Add(parameter);await using var reader=await command.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))assignedCustomerIds.Add(Convert.ToUInt64(reader.GetValue(0)));}
        var term=(q??"").Trim(); var query=_db.Customers.AsNoTracking().Where(x=>assignedCustomerIds.Contains(x.Id)&&x.DeletedAt==null&&x.Active=="Y"&&(x.CustomerType==1||x.CustomerType==3));
        if(term.Length>0)query=query.Where(x=>x.Name.Contains(term)||(x.CustomerCode!=null&&x.CustomerCode.Contains(term))||(x.SapCode!=null&&x.SapCode.Contains(term)));
        var rows=await query.OrderBy(x=>x.Name).Take(25).Select(x=>new{x.Id,code=x.SapCode??x.CustomerCode,name=x.Name,label=(x.SapCode??x.CustomerCode)+" · "+x.Name}).ToListAsync(ct);
        return Ok(new{status="success",data=rows});
    }

    [HttpGet("{distributorId:long}/retailers")]
    public async Task<IActionResult> Retailers(ulong distributorId, CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        // Retailers live in the unified customers table as customertype 2, and their
        // parent dealer is held in custom_fields as the same unified customers.id that
        // the distributor search above returns. There is no id offset to undo.
        command.CommandText = @"SELECT c.id,
COALESCE(NULLIF(JSON_VALUE(c.custom_fields, '$.shop_name'), ''), c.name) AS shop_name,
COALESCE(NULLIF(JSON_VALUE(c.custom_fields, '$.owner_name'), ''), c.first_name) AS owner_name
FROM customers c
WHERE c.customertype = 2
  AND c.deleted_at IS NULL
  AND COALESCE(c.active, 'Y') = 'Y'
  AND (TRY_CONVERT(decimal(20,0), JSON_VALUE(c.custom_fields, '$.distributor_name')) = @distributor_id
       OR TRY_CONVERT(decimal(20,0), JSON_VALUE(c.custom_fields, '$.agri_distributor')) = @distributor_id)
ORDER BY shop_name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@distributor_id";
        parameter.Value = Convert.ToDecimal(distributorId);
        command.Parameters.Add(parameter);
        var rows = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = Convert.ToUInt64(reader.GetValue(0));
            var shop = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var owner = reader.IsDBNull(2) ? "" : reader.GetString(2);
            rows.Add(new { id, name = shop, owner_name = owner, label = string.IsNullOrWhiteSpace(owner) ? shop : $"{shop} · {owner}" });
        }
        return Ok(new { status = "success", data = rows });
    }
}

[ApiController, Authorize, Route("api/reports")]
public sealed class PromotionalActivityReportsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHrRepository _hr;
    public PromotionalActivityReportsController(AppDbContext db, IHrRepository hr) { _db=db; _hr=hr; }
    [HttpGet("all-meeting-summary")]
    public async Task<IActionResult> MeetingSummary([FromQuery]string? date_range,[FromQuery]string? role,[FromQuery]string? zone,[FromQuery]ulong? branch,[FromQuery]ulong? distributor,[FromQuery]DateTime? start_date,[FromQuery]DateTime? end_date,CancellationToken ct)
    {
        var current=ulong.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);var visible=await VisibleIds(current,ct);var visibleLong=visible.Select(x=>checked((long)x)).ToArray();
        var today=DateTime.UtcNow.Date;var q=_db.PromotionalActivities.AsNoTracking().Where(x=>x.DeletedAt==null&&x.Status=="submitted"&&visibleLong.Contains(x.UserId));
        if(date_range=="today")q=q.Where(x=>x.ActivityDate==today);else if(date_range=="year")q=q.Where(x=>x.ActivityDate.Year==today.Year);else if(date_range=="custom"){if(start_date.HasValue)q=q.Where(x=>x.ActivityDate>=start_date.Value.Date);if(end_date.HasValue)q=q.Where(x=>x.ActivityDate<=end_date.Value.Date);}else q=q.Where(x=>x.ActivityDate.Year==today.Year&&x.ActivityDate.Month==today.Month);
        if(branch.HasValue){var branchId=checked((long)branch.Value);q=q.Where(x=>x.BranchId==branchId);}if(distributor.HasValue){var distributorId=checked((long)distributor.Value);q=q.Where(x=>x.DistributorId==distributorId);}
        var designationId=string.Equals(role,"dsr",StringComparison.OrdinalIgnoreCase)?6UL:3UL;
        var raw=await (from x in q join u in _db.Users.AsNoTracking() on (ulong)x.UserId equals u.Id join z in _db.Divisions.AsNoTracking() on u.DivisionId equals (ulong?)z.Id into zj from z in zj.DefaultIfEmpty() join b in _db.Branches.AsNoTracking() on (ulong?)x.BranchId equals (ulong?)b.Id into bj from b in bj.DefaultIfEmpty() join m in _db.Users.AsNoTracking() on (ulong?)x.ReportingManagerId equals (ulong?)m.Id into mj from m in mj.DefaultIfEmpty() where u.DesignationId==designationId&& (string.IsNullOrWhiteSpace(zone)||z.DivisionName==zone) select new{Zone=z==null?null:z.DivisionName,BranchId=x.BranchId,Branch=b==null?null:b.BranchName,x.ActivityType,x.DistributorId,x.DistributorName,x.GiftCount,x.TotalExpense,Participants=x.Participants.Count,UserId=u.Id,UserName=u.Name,EmployeeCode=u.EmployeeCodes,Manager=m==null?null:m.Name}).ToListAsync(ct);
        var rows=raw.GroupBy(x=>new{x.Zone,x.BranchId,x.Branch,x.DistributorId,x.DistributorName,x.UserId,x.UserName,x.EmployeeCode,x.Manager}).Select(g=>new {zone=g.Key.Zone??"Unassigned",branch_id=g.Key.BranchId,branch=g.Key.Branch??"Unassigned",distributor_id=g.Key.DistributorId,distributor_name=g.Key.DistributorName??"Unassigned",user_id=g.Key.UserId,employee_code=g.Key.EmployeeCode,user_name=g.Key.UserName,manager=g.Key.Manager,
            nukkad=Metric(g,"nukkad"),influencer=Metric(g,"influencer"),farmer=Metric(g,"farmer"),retailer=Metric(g,"retailer"),total=new{meets=g.Count(),participants=g.Sum(x=>x.Participants),gifts=g.Sum(x=>x.GiftCount),expense=g.Sum(x=>x.TotalExpense)}}).OrderBy(x=>x.zone).ThenBy(x=>x.distributor_name).ToList();
        var zones=raw.Select(x=>x.Zone).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x=>x).ToArray();var branches=raw.Where(x=>x.BranchId.HasValue).Select(x=>new{id=x.BranchId,name=x.Branch}).Distinct().OrderBy(x=>x.name).ToArray();var distributors=raw.Where(x=>x.DistributorId.HasValue).Select(x=>new{id=x.DistributorId,name=x.DistributorName}).Distinct().OrderBy(x=>x.name).ToArray();
        return Ok(new{status="success",data=new{rows,filters=new{zones,branches,distributors},grand_total=new{meets=raw.Count,participants=raw.Sum(x=>x.Participants),gifts=raw.Sum(x=>x.GiftCount),expense=raw.Sum(x=>x.TotalExpense)}}});
    }

    private async Task<HashSet<ulong>> VisibleIds(ulong current,CancellationToken ct) =>
        (await _hr.GetVisibleUserIdsAsync(current,ct)).ToHashSet();
    [HttpGet("kyc-summary")]
    public async Task<IActionResult> KycSummary(CancellationToken ct)
    {
        var customers=await _db.Customers.AsNoTracking().Where(x=>x.DeletedAt==null&&x.Active=="Y").Select(x=>x.CustomFields).ToListAsync(ct);
        var result=BuildKyc(customers);return Ok(new{status="success",data=result});
    }
    [HttpGet("kyc-summary/asr-wise")]
    public async Task<IActionResult> KycAsrWise(CancellationToken ct)
    {
        var customers=await _db.Customers.AsNoTracking().Where(x=>x.DeletedAt==null&&x.Active=="Y").Select(x=>new{x.CreatedBy,x.CustomFields}).ToListAsync(ct);
        var names=await _db.Users.AsNoTracking().Where(x=>x.DeletedAt==null).ToDictionaryAsync(x=>x.Id,x=>x.Name,ct);
        var rows=customers.GroupBy(x=>x.CreatedBy).Select(g=>new{user_id=g.Key,user_name=g.Key.HasValue&&names.TryGetValue(g.Key.Value,out var name)?name:"Unassigned",summary=BuildKyc(g.Select(x=>x.CustomFields))}).OrderBy(x=>x.user_name).ToList();
        return Ok(new{status="success",data=rows});
    }
    private static object BuildKyc(IEnumerable<string?> values)
    {
        var docs=values.Select(ReadKyc).ToList();var total=docs.Count;object Doc(Func<(bool gst,bool aadhaar,bool pan,bool bank),bool> f){var count=docs.Count(f);return new{count,total,percentage=total==0?0:Math.Round(count*100m/total,2)};}
        var complete=docs.Count(x=>x.gst&&x.aadhaar&&x.pan&&x.bank);return new{total,complete,overall_percentage=total==0?0:Math.Round(complete*100m/total,2),gst=Doc(x=>x.gst),aadhaar=Doc(x=>x.aadhaar),pan=Doc(x=>x.pan),bank=Doc(x=>x.bank)};
    }
    private static (bool gst,bool aadhaar,bool pan,bool bank) ReadKyc(string? json)
    {
        if(string.IsNullOrWhiteSpace(json))return(false,false,false,false);try{using var d=JsonDocument.Parse(json);var r=d.RootElement;bool Has(params string[] keys)=>keys.Any(k=>r.TryGetProperty(k,out var v)&&v.ValueKind!=JsonValueKind.Null&&!string.IsNullOrWhiteSpace(v.ToString()));return(Has("gst_attachment","gst_number","gstin_no"),Has("aadhar_attachment","aadhaar_attachment","aadhar_no","aadhaar_number"),Has("pan_attachment","pan_number","pan_no"),Has("bank_proof","cancelled_cheque","bank_account_number","account_number"));}catch{return(false,false,false,false);}
    }
    private static object Metric(IEnumerable<dynamic> rows,string type){var x=rows.Where(r=>(string)r.ActivityType==type).ToList();return new{meets=x.Count,participants=x.Sum(r=>(int)r.Participants),gifts=x.Sum(r=>(int)r.GiftCount),expense=x.Sum(r=>(decimal)r.TotalExpense)};}
}
