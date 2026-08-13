using Application.DTOs.Hr;
using Application.Common;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public sealed class HrRepository : IHrRepository
{
    private const int MaxRows = 50000;
    private readonly AppDbContext _db;

    public HrRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<HrLookupDto>> GetUsersAsync(ulong? actorUserId, CancellationToken cancellationToken)
    {
        var visibleUserIds = await GetVisibleUserIdsAsync(actorUserId, cancellationToken);
        return await InternalUsersQuery(_db.Users.AsNoTracking())
            .Where(x => x.Active == "Y" && !x.IsDeleted)
            .Where(x => visibleUserIds.Contains(x.Id))
            .OrderBy(x => x.Name)
            .Take(MaxRows)
            .Select(x => new HrLookupDto { Id = x.Id, Name = x.Name })
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HrLookupDto>> GetBranchesAsync(CancellationToken cancellationToken) =>
        await _db.Branches.AsNoTracking().OrderBy(x => x.BranchName)
            .Select(x => new HrLookupDto { Id = x.Id, Name = x.BranchName })
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<HrLookupDto>> GetDivisionsAsync(CancellationToken cancellationToken) =>
        await _db.Divisions.AsNoTracking().OrderBy(x => x.DivisionName)
            .Select(x => new HrLookupDto { Id = x.Id, Name = x.DivisionName })
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<HrLookupDto>> GetDesignationsAsync(CancellationToken cancellationToken) =>
        await _db.Designations.AsNoTracking().OrderBy(x => x.DesignationName)
            .Select(x => new HrLookupDto { Id = x.Id, Name = x.DesignationName })
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<HrLookupDto>> GetDistrictsByUserAsync(ulong userId, CancellationToken cancellationToken) =>
        await (from assign in _db.UserCityAssigns.AsNoTracking()
               join city in _db.Cities.AsNoTracking() on assign.CityId equals city.Id
               join district in _db.Districts.AsNoTracking() on city.DistrictId equals district.Id
               where assign.UserId == userId
               orderby district.DistrictName
               select new HrLookupDto { Id = district.Id, Name = district.DistrictName })
            .Distinct()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<HrLookupDto>> GetCitiesByUserAndDistrictAsync(ulong userId, ulong districtId, CancellationToken cancellationToken) =>
        await (from assign in _db.UserCityAssigns.AsNoTracking()
               join city in _db.Cities.AsNoTracking() on assign.CityId equals city.Id
               where assign.UserId == userId && city.DistrictId == districtId
               orderby city.CityName
               select new HrLookupDto { Id = city.Id, Name = city.CityName })
            .Distinct()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<HolidayDto>> GetHolidaysAsync(HolidayListFilterDto filter, CancellationToken cancellationToken)
    {
        var query = from h in _db.Holidays.AsNoTracking()
                    join branch in _db.Branches.AsNoTracking() on h.Branch equals branch.Id into branches
                    from branch in branches.DefaultIfEmpty()
                    join division in _db.Divisions.AsNoTracking() on h.DivisionId equals division.Id into divisions
                    from division in divisions.DefaultIfEmpty()
                    join creator in _db.Users.AsNoTracking() on h.CreatedBy equals creator.Id into creators
                    from creator in creators.DefaultIfEmpty()
                    select new { h, branch, division, creator };

        if (!string.IsNullOrWhiteSpace(filter.HolidayFor))
        {
            var holidayFor = string.Equals(filter.HolidayFor, "division", StringComparison.OrdinalIgnoreCase) || string.Equals(filter.HolidayFor, "zone", StringComparison.OrdinalIgnoreCase)
                ? "division"
                : "branch";
            query = query.Where(x => x.h.HolidayFor == holidayFor);
        }
        if (filter.BranchId.HasValue) query = query.Where(x => x.h.HolidayFor == "branch" && x.h.Branch == filter.BranchId);
        if (filter.DivisionId.HasValue) query = query.Where(x => x.h.HolidayFor == "division" && x.h.DivisionId == filter.DivisionId);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(x => (x.h.Name ?? "").Contains(search) || (x.h.HolidayDate ?? "").Contains(search) || (x.branch.BranchName ?? "").Contains(search) || (x.division.DivisionName ?? "").Contains(search));
        }

        return await query.OrderByDescending(x => x.h.Id)
            .Select(x => new HolidayDto
            {
                Id = x.h.Id,
                Active = x.h.Active,
                Branch = x.h.Branch,
                BranchName = x.branch.BranchName,
                HolidayFor = x.h.HolidayFor,
                DivisionId = x.h.DivisionId,
                DivisionName = x.division.DivisionName,
                Name = x.h.Name,
                HolidayDate = x.h.HolidayDate,
                Names = SplitCsv(x.h.Name),
                HolidayDates = SplitCsv(x.h.HolidayDate),
                CreatedBy = x.h.CreatedBy,
                CreatedByName = x.creator.Name,
                CreatedAt = x.h.CreatedAt
            })
            .ToListAsync(cancellationToken);
    }

    public Task<Holiday?> GetHolidayEntityAsync(ulong id, CancellationToken cancellationToken) =>
        _db.Holidays.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<HolidayDto?> GetHolidayAsync(ulong id, CancellationToken cancellationToken) =>
        (await GetHolidaysAsync(new HolidayListFilterDto(), cancellationToken)).FirstOrDefault(x => x.Id == id);

    public async Task AddHolidayAsync(Holiday holiday, CancellationToken cancellationToken)
    {
        _db.Holidays.Add(holiday);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteHolidayAsync(Holiday holiday, ulong? actorUserId, CancellationToken cancellationToken)
    {
        holiday.DeletedAt = DateTime.UtcNow;
        holiday.UpdatedBy = actorUserId;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LeaveDto>> GetLeavesAsync(LeaveListFilterDto filter, CancellationToken cancellationToken)
    {
        var query = from leave in _db.Leaves.AsNoTracking()
                    join user in _db.Users.AsNoTracking() on leave.UserId equals user.Id into users
                    from user in users.DefaultIfEmpty()
                    join creator in _db.Users.AsNoTracking() on leave.CreatedBy equals creator.Id into creators
                    from creator in creators.DefaultIfEmpty()
                    join approver in _db.Users.AsNoTracking() on leave.ApprovedBy equals approver.Id into approvers
                    from approver in approvers.DefaultIfEmpty()
                    select new { leave, user, creator, approver };

        if (filter.ExecutiveId.HasValue) query = query.Where(x => x.leave.UserId == filter.ExecutiveId);
        if (filter.StartDate.HasValue) query = query.Where(x => x.leave.ToDate >= filter.StartDate.Value.Date);
        if (filter.EndDate.HasValue) query = query.Where(x => x.leave.FromDate <= filter.EndDate.Value.Date);
        if (int.TryParse(filter.Status, out var status)) query = query.Where(x => x.leave.Status == status);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(x => (x.user.Name ?? "").Contains(search) || (x.leave.Type ?? "").Contains(search) || (x.leave.Reason ?? "").Contains(search));
        }

        return await query.OrderByDescending(x => x.leave.Id)
            .Select(x => new LeaveDto
            {
                Id = x.leave.Id,
                Active = x.leave.Active,
                UserId = x.leave.UserId,
                UserName = x.user.Name,
                EmployeeCode = x.user.EmployeeCodes,
                FromDate = x.leave.FromDate,
                ToDate = x.leave.ToDate,
                Type = x.leave.Type,
                BalType = x.leave.BalType,
                Reason = x.leave.Reason,
                Status = x.leave.Status,
                StatusLabel = x.leave.Status == 1 ? "Approved" : x.leave.Status == 2 ? "Rejected" : "Pending",
                RemarkStatus = x.leave.RemarkStatus,
                CreatedBy = x.leave.CreatedBy,
                CreatedByName = x.creator.Name,
                CreatedAt = x.leave.CreatedAt,
                ApprovedBy = x.leave.ApprovedBy,
                ApprovedByName = x.approver.Name,
                ApprovedAt = x.leave.ApprovedAt
            })
            .ToListAsync(cancellationToken);
    }

    public Task<Leave?> GetLeaveEntityAsync(ulong id, CancellationToken cancellationToken) =>
        _db.Leaves.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<LeaveDto?> GetLeaveAsync(ulong id, CancellationToken cancellationToken) =>
        (await GetLeavesAsync(new LeaveListFilterDto(), cancellationToken)).FirstOrDefault(x => x.Id == id);

    public async Task AddLeaveAsync(Leave leave, CancellationToken cancellationToken)
    {
        _db.Leaves.Add(leave);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteLeaveAsync(Leave leave, CancellationToken cancellationToken)
    {
        _db.Leaves.Remove(leave);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TourDto>> GetToursAsync(TourListFilterDto filter, IReadOnlyCollection<ulong>? allowedUserIds, CancellationToken cancellationToken)
    {
        var query = from tour in _db.TourProgrammes.AsNoTracking()
                    join user in _db.Users.AsNoTracking() on tour.UserId equals user.Id into users
                    from user in users.DefaultIfEmpty()
                    select new { tour, user };

        if (allowedUserIds is not null)
        {
            if (allowedUserIds.Count == 0) return [];
            query = query.Where(x => x.tour.UserId.HasValue && allowedUserIds.Contains(x.tour.UserId.Value));
        }
        if (filter.ExecutiveId.HasValue) query = query.Where(x => x.tour.UserId == filter.ExecutiveId);
        if (filter.DivisionId.HasValue) query = query.Where(x => x.user.DivisionId == filter.DivisionId);
        if (filter.DesignationId.HasValue) query = query.Where(x => x.user.DesignationId == filter.DesignationId);
        if (filter.StartDate.HasValue) query = query.Where(x => x.tour.Date >= filter.StartDate.Value.Date);
        if (filter.EndDate.HasValue) query = query.Where(x => x.tour.Date <= filter.EndDate.Value.Date);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(x => (x.user.Name ?? "").Contains(search) || x.tour.Objectives.Contains(search) || x.tour.Type.Contains(search));
        }

        var rows = await query.OrderByDescending(x => x.tour.Date ?? DateTime.MinValue).ThenBy(x => x.tour.Date)
            .Select(x => new { x.tour, UserName = x.user.Name, EmployeeCode = x.user.EmployeeCodes, x.user.DesignationId, x.user.PrimaryBranchId, x.user.BranchId, x.user.ReportingId })
            .ToListAsync(cancellationToken);

        var cityIds = rows.Select(x => ParseUlong(x.tour.Town)).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var districtIds = rows.Select(x => ToUlong(x.tour.District)).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var cities = await _db.Cities.AsNoTracking()
            .Where(x => cityIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.CityName, cancellationToken);
        var districts = await _db.Districts.AsNoTracking()
            .Where(x => districtIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DistrictName, cancellationToken);

        var tourIds = rows.Select(x => x.tour.Id).ToArray();
        var details = await _db.TourDetails.AsNoTracking().Where(x => x.TourId.HasValue && tourIds.Contains(x.TourId.Value) && x.VisitedCityId.HasValue)
            .Select(x => new { TourId = x.TourId!.Value, CityId = x.VisitedCityId!.Value }).Distinct().ToListAsync(cancellationToken);
        var actualCityIds = details.Select(x => x.CityId).Distinct().ToArray();
        var actualCities = await _db.Cities.AsNoTracking().Where(x => actualCityIds.Contains(x.Id)).Select(x => new { x.Id, x.CityName, x.DistrictId }).ToListAsync(cancellationToken);
        var actualDistrictIds = actualCities.Where(x => x.DistrictId.HasValue).Select(x => x.DistrictId!.Value).Distinct().ToArray();
        var actualDistricts = await _db.Districts.AsNoTracking().Where(x => actualDistrictIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DistrictName, cancellationToken);
        var designationIds = rows.Where(x => x.DesignationId.HasValue).Select(x => x.DesignationId!.Value).Distinct().ToArray();
        var designationNames = await _db.Designations.AsNoTracking().Where(x => designationIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DesignationName, cancellationToken);
        var branchIds = rows.Where(x => x.PrimaryBranchId.HasValue).Select(x => x.PrimaryBranchId!.Value).Distinct().ToArray();
        var branchNames = await _db.Branches.AsNoTracking().Where(x => branchIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.BranchName, cancellationToken);
        var reportingIds = rows.Where(x => x.ReportingId.HasValue).Select(x => x.ReportingId!.Value).Distinct().ToArray();
        var reportingNames = await _db.Users.AsNoTracking().Where(x => reportingIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        return rows.SelectMany(x =>
        {
            var cityId = ParseUlong(x.tour.Town);
            var districtId = ToUlong(x.tour.District);
            var visited = details.Where(d => d.TourId == x.tour.Id).Select(d => actualCities.FirstOrDefault(c => c.Id == d.CityId)).Where(c => c is not null).DistinctBy(c => c!.Id).ToList();
            if (visited.Count == 0) visited.Add(null);
            return visited.Select(actual => new TourDto
            {
                Id = x.tour.Id,
                Date = x.tour.Date,
                UserId = x.tour.UserId,
                UserName = x.UserName,
                EmployeeCode = x.EmployeeCode,
                BranchName = x.PrimaryBranchId.HasValue && branchNames.TryGetValue(x.PrimaryBranchId.Value, out var branch) ? branch : null,
                DesignationName = x.DesignationId.HasValue && designationNames.TryGetValue(x.DesignationId.Value, out var designation) ? designation : null,
                ReportingManager = x.ReportingId.HasValue && reportingNames.TryGetValue(x.ReportingId.Value, out var manager) ? manager : null,
                Town = x.tour.Town,
                TownName = cityId.HasValue && cities.TryGetValue(cityId.Value, out var cityName) ? cityName : x.tour.Town,
                District = x.tour.District?.ToString(),
                DistrictName = districtId.HasValue && districts.TryGetValue(districtId.Value, out var districtName) ? districtName : x.tour.District?.ToString(),
                ActualTownName = actual?.CityName,
                ActualDistrictName = actual?.DistrictId.HasValue == true && actualDistricts.TryGetValue(actual.DistrictId.Value, out var actualDistrict) ? actualDistrict : null,
                IsDeviation = actual is null || !cityId.HasValue ? null : actual.Id != cityId.Value,
                Objectives = x.tour.Objectives,
                Type = x.tour.Type,
                Status = x.tour.Status.ToString(),
                StatusLabel = x.tour.Status == 1 ? "Approved" : x.tour.Status == 2 ? "Rejected" : "Pending",
                CreatedAt = x.tour.CreatedAt
            });
        }).ToList();
    }

    public Task<TourProgramme?> GetTourEntityAsync(ulong id, CancellationToken cancellationToken) =>
        _db.TourProgrammes.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<TourDto?> GetTourAsync(ulong id, CancellationToken cancellationToken) =>
        (await GetToursAsync(new TourListFilterDto(), null, cancellationToken)).FirstOrDefault(x => x.Id == id);

    public async Task AddTourAsync(TourProgramme tour, CancellationToken cancellationToken)
    {
        _db.TourProgrammes.Add(tour);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteTourAsync(TourProgramme tour, CancellationToken cancellationToken)
    {
        _db.TourProgrammes.Remove(tour);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddTourLogAsync(TourLog log, CancellationToken cancellationToken)
    {
        _db.TourLogs.Add(log);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertTourDetailAsync(ulong tourId, ulong cityId, CancellationToken cancellationToken)
    {
        var detail = await _db.TourDetails.FirstOrDefaultAsync(x => x.TourId == tourId && x.CityId == cityId, cancellationToken);
        if (detail is null)
        {
            _db.TourDetails.Add(new TourDetail { TourId = tourId, CityId = cityId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        }
        else
        {
            detail.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<AttendanceDto>> GetAttendancesAsync(AttendanceListFilterDto filter, CancellationToken cancellationToken)
    {
        var visibleUserIds = await GetVisibleUserIdsAsync(filter.ActorUserId, cancellationToken);
        var query = from attendance in _db.Attendances.AsNoTracking()
                    join user in _db.Users.AsNoTracking() on attendance.UserId equals user.Id into users
                    from user in users.DefaultIfEmpty()
                    join branch in _db.Branches.AsNoTracking() on user.PrimaryBranchId equals branch.Id into branches
                    from branch in branches.DefaultIfEmpty()
                    join designation in _db.Designations.AsNoTracking() on user.DesignationId equals designation.Id into designations
                    from designation in designations.DefaultIfEmpty()
                    join division in _db.Divisions.AsNoTracking() on user.DivisionId equals division.Id into divisions
                    from division in divisions.DefaultIfEmpty()
                    join reporting in _db.Users.AsNoTracking() on user.ReportingId equals reporting.Id into reportings
                    from reporting in reportings.DefaultIfEmpty()
                    select new { attendance, user, branch, designation, division, reporting };

        query = query.Where(x => x.attendance.UserId.HasValue && visibleUserIds.Contains(x.attendance.UserId.Value));

        if (filter.ExecutiveId.HasValue) query = query.Where(x => x.attendance.UserId == filter.ExecutiveId);
        if (filter.DesignationId.HasValue) query = query.Where(x => x.user.DesignationId == filter.DesignationId);
        if (filter.BranchId.HasValue)
        {
            var branchId = filter.BranchId.Value.ToString();
            query = query.Where(x => x.user.PrimaryBranchId == filter.BranchId ||
                (x.user.BranchId != null && (x.user.BranchId == branchId || x.user.BranchId.StartsWith(branchId + ",") ||
                 x.user.BranchId.EndsWith("," + branchId) || x.user.BranchId.Contains("," + branchId + ","))));
        }
        if (filter.DivisionId.HasValue) query = query.Where(x => x.user.DivisionId == filter.DivisionId);
        if (!string.IsNullOrWhiteSpace(filter.Active)) query = query.Where(x => x.user.Active == filter.Active);
        if (filter.StartDate.HasValue) query = query.Where(x => x.attendance.PunchinDate >= filter.StartDate.Value.Date);
        if (filter.EndDate.HasValue) query = query.Where(x => x.attendance.PunchinDate <= filter.EndDate.Value.Date);
        if (int.TryParse(filter.Status, out var status)) query = query.Where(x => x.attendance.AttendanceStatus == status);
        if (string.Equals(filter.Type, "leave", StringComparison.OrdinalIgnoreCase))
            query = query.Where(x => x.attendance.WorkingType == "Full Day Leave" || x.attendance.WorkingType == "First Half Leave" || x.attendance.WorkingType == "Second Half Leave" || x.attendance.WorkingType == "Leave");
        else if (string.Equals(filter.Type, "attendance", StringComparison.OrdinalIgnoreCase))
            query = query.Where(x => x.attendance.WorkingType != "Full Day Leave" && x.attendance.WorkingType != "First Half Leave" && x.attendance.WorkingType != "Second Half Leave" && x.attendance.WorkingType != "Leave");
        else if (!string.IsNullOrWhiteSpace(filter.Type)) query = query.Where(x => x.attendance.WorkingType == filter.Type);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(x => (x.user.Name ?? "").Contains(search) || x.attendance.WorkingType.Contains(search) || x.attendance.PunchinSummary.Contains(search));
        }

        var page = Pagination.Page(filter.Page);
        var pageSize = Pagination.PageSize(filter.PageSize);
        var total = await query.LongCountAsync(cancellationToken);
        var orderedQuery = query.OrderByDescending(x => x.attendance.PunchinDate).ThenByDescending(x => x.attendance.Id);
        var pagedQuery = filter.Unpaged
            ? orderedQuery
            : orderedQuery.Skip((page - 1) * pageSize).Take(pageSize);

        var rows = await pagedQuery
            .Select(x => new AttendanceDto
            {
                Id = x.attendance.Id,
                UserId = x.attendance.UserId,
                UserName = x.user.Name,
                EmployeeCode = x.user.EmployeeCodes,
                BranchName = x.branch.BranchName,
                LegacyBranchIds = x.user.BranchId,
                DesignationName = x.designation.DesignationName,
                DivisionName = x.division.DivisionName,
                ReportingManager = x.reporting.Name,
                ApproveRejectBy = x.attendance.ApproveRejectBy,
                PunchinDate = x.attendance.PunchinDate,
                PunchinTime = x.attendance.PunchinTime.ToString(@"hh\:mm"),
                PunchoutDate = x.attendance.PunchoutDate,
                PunchoutTime = x.attendance.PunchoutTime == null ? null : x.attendance.PunchoutTime.Value.ToString(@"hh\:mm"),
                WorkedTime = x.attendance.WorkedTime,
                WorkingType = x.attendance.WorkingType,
                AttendanceStatus = x.attendance.AttendanceStatus,
                AttendanceStatusLabel = x.attendance.AttendanceStatus == 1 ? "Approved" : x.attendance.AttendanceStatus == 2 ? "Rejected" : "Pending",
                AttendanceLabel = x.attendance.PunchoutTime == null ? "Misspunch" : "Present",
                RemarkStatus = x.attendance.RemarkStatus,
                PunchinSummary = x.attendance.PunchinSummary,
                PunchoutSummary = x.attendance.PunchoutSummary,
                PunchinAddress = string.IsNullOrEmpty(x.attendance.PunchinAddress)
                    ? ((x.attendance.PunchinLatitude ?? "") + (string.IsNullOrEmpty(x.attendance.PunchinLatitude) || string.IsNullOrEmpty(x.attendance.PunchinLongitude) ? "" : ", ") + (x.attendance.PunchinLongitude ?? ""))
                    : x.attendance.PunchinAddress,
                PunchoutAddress = string.IsNullOrEmpty(x.attendance.PunchoutAddress)
                    ? ((x.attendance.PunchoutLatitude ?? "") + (string.IsNullOrEmpty(x.attendance.PunchoutLatitude) || string.IsNullOrEmpty(x.attendance.PunchoutLongitude) ? "" : ", ") + (x.attendance.PunchoutLongitude ?? ""))
                    : x.attendance.PunchoutAddress,
                PunchinFrom = x.attendance.PunchinFrom,
                CreatedAt = x.attendance.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var missingBranchIds = rows.Where(x => string.IsNullOrWhiteSpace(x.BranchName))
            .Select(x => ParseUlong(SplitCsv(x.LegacyBranchIds).FirstOrDefault()))
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        if (missingBranchIds.Length > 0)
        {
            var branchNames = await _db.Branches.AsNoTracking().Where(x => missingBranchIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.BranchName, cancellationToken);
            foreach (var row in rows.Where(x => string.IsNullOrWhiteSpace(x.BranchName)))
            {
                var id = ParseUlong(SplitCsv(row.LegacyBranchIds).FirstOrDefault());
                if (id.HasValue && branchNames.TryGetValue(id.Value, out var name)) row.BranchName = name;
            }
        }

        var approverIds = rows.Select(x => ParseUlong(x.ApproveRejectBy)).Where(x => x.HasValue)
            .Select(x => x!.Value).Distinct().ToArray();
        if (approverIds.Length > 0)
        {
            var approverNames = await _db.Users.AsNoTracking().Where(x => approverIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
            foreach (var row in rows)
            {
                var id = ParseUlong(row.ApproveRejectBy);
                if (id.HasValue && approverNames.TryGetValue(id.Value, out var name)) row.ApproveRejectByName = name;
            }
        }
        return new PagedResult<AttendanceDto>(rows, total, page, filter.Unpaged ? rows.Count : pageSize);
    }

    public async Task<AttendancePlanResponseDto> GetAttendancePlanAsync(ulong userId, DateTime date, CancellationToken cancellationToken)
    {
        var tour = await _db.TourProgrammes.AsNoTracking()
            .Where(x => x.UserId == userId && x.Date == date.Date)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        AttendanceTourPlanDto? tourData = null;
        if (tour is not null)
        {
            var cityId = ParseUlong(tour.Town);
            var cityName = cityId.HasValue
                ? await _db.Cities.AsNoTracking()
                    .Where(x => x.Id == cityId.Value)
                    .Select(x => x.CityName)
                    .FirstOrDefaultAsync(cancellationToken)
                : null;

            tourData = new AttendanceTourPlanDto
            {
                Id = tour.Id,
                Name = string.IsNullOrWhiteSpace(tour.Objectives) ? "Tour Plan" : tour.Objectives,
                Objectives = string.IsNullOrWhiteSpace(tour.Objectives) ? "-" : tour.Objectives,
                CityName = string.IsNullOrWhiteSpace(cityName) ? "-" : cityName,
                CityId = tour.Town
            };
        }

        var beatRows = await (from schedule in _db.BeatSchedules.AsNoTracking()
                              join beat in _db.Beats.AsNoTracking() on schedule.BeatId equals beat.Id
                              where schedule.UserId == userId && schedule.BeatDate == date.Date
                              orderby beat.BeatName
                              select new { beat.BeatName, beat.Description, beat.CityId })
            .ToListAsync(cancellationToken);

        AttendanceBeatPlanDto? beatData = null;
        if (beatRows.Count > 0)
        {
            var mainBeat = beatRows[0];
            var beatCityId = ParseUlong(mainBeat.CityId);
            var beatCityName = beatCityId.HasValue
                ? await _db.Cities.AsNoTracking()
                    .Where(x => x.Id == beatCityId.Value)
                    .Select(x => x.CityName)
                    .FirstOrDefaultAsync(cancellationToken)
                : null;

            beatData = new AttendanceBeatPlanDto
            {
                BeatName = string.Join(", ", beatRows.Select(x => x.BeatName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()),
                AreaTown = string.IsNullOrWhiteSpace(beatCityName) ? "-" : beatCityName,
                CityId = mainBeat.CityId,
                Description = string.IsNullOrWhiteSpace(mainBeat.Description) ? "-" : mainBeat.Description
            };
        }

        return new AttendancePlanResponseDto
        {
            Tour = new AttendancePlanSectionDto { Exists = tourData is not null, Data = tourData },
            Beat = new AttendanceBeatSectionDto { Exists = beatData is not null, Data = beatData }
        };
    }

    public async Task DeleteAttendanceAsync(Attendance attendance, CancellationToken cancellationToken)
    {
        _db.Attendances.Remove(attendance);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<Attendance?> GetAttendanceEntityAsync(ulong id, CancellationToken cancellationToken) =>
        _db.Attendances.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<Attendance?> GetAttendanceByUserDateAsync(ulong userId, DateTime date, CancellationToken cancellationToken) =>
        _db.Attendances.FirstOrDefaultAsync(x => x.UserId == userId && x.PunchinDate == date.Date, cancellationToken);

    public async Task AddAttendanceAsync(Attendance attendance, CancellationToken cancellationToken)
    {
        _db.Attendances.Add(attendance);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Attendance>> GetAttendanceEntitiesInRangeAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken) =>
        await _db.Attendances.AsNoTracking()
            .Where(x => x.PunchinDate >= startDate.Date && x.PunchinDate <= endDate.Date)
            .ToListAsync(cancellationToken);

    public Task<User?> GetUserAsync(ulong id, CancellationToken cancellationToken) =>
        _db.Users.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<IReadOnlyCollection<ulong>> GetVisibleUserIdsAsync(ulong? actorUserId, CancellationToken cancellationToken) =>
        ReportingVisibility.GetVisibleUserIdsAsync(_db, actorUserId, cancellationToken);

    public async Task<IReadOnlyList<User>> GetReportUsersAsync(AttendanceListFilterDto filter, CancellationToken cancellationToken)
    {
        var visibleUserIds = await GetVisibleUserIdsAsync(filter.ActorUserId, cancellationToken);
        var query = _db.Users.AsNoTracking().Where(x => x.Active == "Y" && !x.IsDeleted && x.ShowAttandanceReport == "1");
        query = query.Where(x => visibleUserIds.Contains(x.Id));
        if (filter.ExecutiveId.HasValue) query = query.Where(x => x.Id == filter.ExecutiveId);
        if (filter.DesignationId.HasValue) query = query.Where(x => x.DesignationId == filter.DesignationId);
        if (filter.BranchId.HasValue) query = query.Where(x => x.PrimaryBranchId == filter.BranchId);
        if (filter.DivisionId.HasValue) query = query.Where(x => x.DivisionId == filter.DivisionId);
        return await query.OrderBy(x => x.Name).Take(MaxRows).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Holiday>> GetActiveHolidaysAsync(CancellationToken cancellationToken) =>
        await _db.Holidays.AsNoTracking().Where(x => x.Active == "Y").ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Leave>> GetLeavesForUserDateRangeAsync(ulong userId, DateTime startDate, DateTime endDate, CancellationToken cancellationToken) =>
        await _db.Leaves.AsNoTracking()
            .Where(x => x.UserId == userId && x.ToDate >= startDate.Date && x.FromDate <= endDate.Date)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CompOffLeave>> GetAvailableCompOffsAsync(ulong userId, CancellationToken cancellationToken) =>
        await _db.CompOffLeaves
            .Where(x => x.UserId == (long)userId && !x.IsUsed && x.ExpiryDate >= DateTime.Today && x.Balance > 0)
            .OrderBy(x => x.ExpiryDate)
            .ToListAsync(cancellationToken);

    public async Task AddCompOffAsync(CompOffLeave compOff, CancellationToken cancellationToken)
    {
        _db.CompOffLeaves.Add(compOff);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);

    private static string[] SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static ulong? ParseUlong(string? value) =>
        ulong.TryParse(value, out var id) && id > 0 ? id : null;

    private static ulong? ToUlong(long? value) =>
        value.HasValue && value.Value > 0 ? (ulong)value.Value : null;

    private IQueryable<User> InternalUsersQuery(IQueryable<User> query) =>
        ReportingVisibility.InternalUsersQuery(_db, query);
}
