using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SqlServer2022Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = 160;",
                suppressTransaction: true);

            migrationBuilder.Sql(
                """
                SET ANSI_NULLS ON;
                SET ANSI_PADDING ON;
                SET ANSI_WARNINGS ON;
                SET ARITHABORT ON;
                SET CONCAT_NULL_YIELDS_NULL ON;
                SET QUOTED_IDENTIFIER ON;
                SET NUMERIC_ROUNDABORT OFF;
                """);

            migrationBuilder.CreateTable(
                name: "attendances",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    user_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    punchin_date = table.Column<DateTime>(type: "date", nullable: false),
                    punchin_time = table.Column<TimeSpan>(type: "time", nullable: false),
                    punchin_longitude = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    punchin_latitude = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    punchin_address = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    punchin_image = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    punchout_date = table.Column<DateTime>(type: "date", nullable: true),
                    punchout_time = table.Column<TimeSpan>(type: "time", nullable: true),
                    punchout_latitude = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    punchout_longitude = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    punchout_address = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    punchout_image = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    punchin_summary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    punchout_summary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    worked_time = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    working_type = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    attendance_status = table.Column<int>(type: "int", nullable: true),
                    remark_status = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    approve_reject_by = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    punchin_from = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    flag = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    beat_id = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    tourid = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    city = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "beat_schedules",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    beat_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    beat_date = table.Column<DateTime>(type: "date", nullable: true),
                    user_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    tourid = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_beat_schedules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "beats",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    beat_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    description = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    city_id = table.Column<string>(type: "nvarchar(225)", maxLength: 225, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_beats", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "branches",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    branch_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    branch_code = table.Column<string>(type: "nvarchar(125)", maxLength: 125, nullable: true),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    updated_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_branches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    ranking = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    category_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    category_image = table.Column<string>(type: "nvarchar(350)", maxLength: 350, nullable: false),
                    sap_code = table.Column<string>(type: "nvarchar(350)", maxLength: 350, nullable: true),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    updated_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cities",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    city_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    district_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    state_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    grade = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    updated_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "comp_off_leaves",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    user_id = table.Column<long>(type: "bigint", nullable: true),
                    leave_id = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    comp_off_date = table.Column<DateTime>(type: "date", nullable: true),
                    expiry_date = table.Column<DateTime>(type: "date", nullable: true),
                    is_used = table.Column<bool>(type: "bit", nullable: false),
                    balance = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false, defaultValue: 1m),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comp_off_leaves", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "countries",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    country_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    updated_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_countries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    first_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false, defaultValue: ""),
                    last_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false, defaultValue: ""),
                    mobile = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    contact_number = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    email = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    password = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false, defaultValue: ""),
                    notification_id = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false, defaultValue: ""),
                    latitude = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    longitude = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    device_type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: ""),
                    gender = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: ""),
                    profile_image = table.Column<string>(type: "nvarchar(350)", maxLength: 350, nullable: false, defaultValue: ""),
                    shop_image = table.Column<string>(type: "nvarchar(350)", maxLength: 350, nullable: true),
                    customer_code = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false, defaultValue: ""),
                    status_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    customertype = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    region_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    firmtype = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    updated_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    executive_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    beatscheduleid = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    manager_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false, defaultValue: ""),
                    manager_phone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: ""),
                    otp = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    custom_fields = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    same_address = table.Column<bool>(type: "bit", nullable: true),
                    parent_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    sap_code = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "departments",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    updated_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_departments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "designations",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    designation_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    updated_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_designations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "districts",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    district_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    state_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    updated_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_districts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "divisions",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    division_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    updated_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_divisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "expense_logs",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    log_date = table.Column<DateOnly>(type: "date", nullable: true),
                    expense_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    status_type = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expense_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "expenses",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    expenses_type = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    user_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    date = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    claim_amount = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: true),
                    approve_amount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    start_km = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    stop_km = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    total_km = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    note = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    checker_status = table.Column<int>(type: "int", nullable: false),
                    accountant_status = table.Column<int>(type: "int", nullable: false),
                    approve_reject_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    reason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expenses", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "expenses_types",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    rate = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: false),
                    is_active = table.Column<int>(type: "int", nullable: false),
                    allowance_type_id = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    payroll_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expenses_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "holidays",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    name = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    holiday_date = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    branch = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    holiday_for = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "branch"),
                    division_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    updated_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holidays", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leaves",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    user_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    from_date = table.Column<DateTime>(type: "date", nullable: false),
                    to_date = table.Column<DateTime>(type: "date", nullable: false),
                    type = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    bal_type = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    status = table.Column<int>(type: "int", nullable: true),
                    remark_status = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leaves", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_redemptions",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    transaction_no = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    customer_id = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    loyalty_scheme_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    wallet_type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    scheme_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    redeem_mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    points = table.Column<decimal>(type: "decimal(15,2)", precision: 15, scale: 2, nullable: false),
                    account_holder = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    account_number = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    bank_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ifsc_code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    bank_confirmed = table.Column<bool>(type: "bit", nullable: false),
                    status = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    remark = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    approved_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    approved_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    rejected_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    rejected_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_redemptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_schemes",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    scheme_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    scheme_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    scheme_description = table.Column<string>(type: "text", nullable: true),
                    scheme_tag = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    customer_type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    area_scope = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    area_values = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    scheme_type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: "Invoice"),
                    based_on = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    redemption_enabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    brochure_path = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    submitted_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    submitted_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    approved_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    approved_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    approval_remark = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    rejected_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    rejected_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    rejection_remark = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    updated_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_schemes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "media",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    model_type = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    model_id = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    uuid = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    collection_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    file_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    mime_type = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    disk = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    conversions_disk = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    size = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    manipulations = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    custom_properties = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    generated_conversions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    responsive_images = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    order_column = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mobile_user_login_details",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    user_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    customer_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    app_version = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    device_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    device_type = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    unique_id = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    first_login_date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    last_login_date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    login_status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    multi_login = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    app = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    login_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_user_login_details", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "new_invoice_approval_logs",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    log_date = table.Column<DateTime>(type: "date", nullable: true),
                    new_invoice_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    status_type = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    from_status = table.Column<int>(type: "int", nullable: true),
                    to_status = table.Column<int>(type: "int", nullable: true),
                    approved_amount = table.Column<decimal>(type: "decimal(15,2)", precision: 15, scale: 2, nullable: true),
                    remark = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_new_invoice_approval_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "new_invoices",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    secondary_customer_id = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    loyalty_scheme_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    invoice_number = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    invoice_date = table.Column<DateTime>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "decimal(15,2)", precision: 15, scale: 2, nullable: false),
                    points = table.Column<decimal>(type: "decimal(15,2)", precision: 15, scale: 2, nullable: false, defaultValue: 0m),
                    attachment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    approval_status = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    approval_remark = table.Column<string>(type: "text", nullable: true),
                    approved_ss_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    approved_ss_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    approved_sales_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    approved_sales_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    approved_ho_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    approved_ho_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    rejected_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    rejected_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_new_invoices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "oauth_access_tokens",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    user_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    client_id = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    scopes = table.Column<string>(type: "text", nullable: true),
                    revoked = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    expires_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_access_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "order_details",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    order_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    product_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    product_detail_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    shipped_qty = table.Column<long>(type: "bigint", nullable: false),
                    price = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: false),
                    discount = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: false),
                    gst = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: false),
                    gst_amount = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: false),
                    discount_amount = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: false),
                    tax_amount = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: false),
                    line_total = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: false),
                    status_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    scheme_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    scheme_discount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    scheme_amount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    cluster_discount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    cluster_amount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    deal_discount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    deal_amount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    distributor_discount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    distributor_amount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    frieght_discount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    frieght_amount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    agri_standard_dis = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    agri_standard_dis_amounts = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    ebd_dis = table.Column<int>(type: "int", nullable: true),
                    special_dis = table.Column<int>(type: "int", nullable: true),
                    special_amounts = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    ebd_amount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    subcategory_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    category_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_details", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    buyer_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    seller_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    executive_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    shipped_qty = table.Column<long>(type: "bigint", nullable: false),
                    orderno = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false, defaultValue: ""),
                    order_date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    completed_date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    estimated_date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    total_gst = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: false),
                    total_discount = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: false),
                    total_amount = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: false),
                    extra_discount = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: false),
                    extra_discount_amount = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: false),
                    grand_total = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: false),
                    order_taking = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false, defaultValue: ""),
                    status_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    address_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    suc_del = table.Column<string>(type: "nvarchar(191)", maxLength: 191, nullable: true),
                    gst_amount = table.Column<string>(type: "nvarchar(125)", maxLength: 125, nullable: true),
                    schme_amount = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: true),
                    schme_val = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: true),
                    ebd_amount = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: true),
                    ebd_discount = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: true),
                    special_discount = table.Column<int>(type: "int", nullable: true),
                    special_amount = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: true),
                    cluster_discount = table.Column<int>(type: "int", nullable: true),
                    cluster_amount = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: true),
                    deal_discount = table.Column<int>(type: "int", nullable: true),
                    deal_amount = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: true),
                    distributor_discount = table.Column<int>(type: "int", nullable: true),
                    distributor_amount = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: true),
                    frieght_discount = table.Column<int>(type: "int", nullable: true),
                    frieght_amount = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: true),
                    product_cat_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    dod_discount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    cash_discount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    cash_amount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    agri_standard_discount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    agri_standard_discount_amount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    advance = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: false),
                    gst5_amt = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    gst12_amt = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    gst18_amt = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    gst28_amt = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    order_remark = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    updated_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    beatscheduleid = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    order_type = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    sub_total = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: false),
                    total_qty = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "permissions",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    guard_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permissions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pincodes",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    pincode = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    city_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    updated_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pincodes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "primary_sales",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    invoice_date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    branch_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    emp_code = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    net_amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    quantity = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_primary_sales", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product_details",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    detail_title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    detail_description = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    product_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    detail_image = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    mrp = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: true),
                    price = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: true),
                    selling_price = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_details", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    ranking = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    product_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    product_code = table.Column<string>(type: "nvarchar(125)", maxLength: 125, nullable: true),
                    display_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    description = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    subcategory_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    category_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    product_image = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    updated_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    specification = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    part_no = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    product_no = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    model_no = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    sap_code = table.Column<string>(type: "nvarchar(225)", maxLength: 225, nullable: true),
                    hsn_sac = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "role_has_permissions",
                columns: table => new
                {
                    permission_id = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    role_id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_has_permissions", x => new { x.permission_id, x.role_id });
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    guard_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "salestargetusers",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    user_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    branch_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    type = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    month = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    year = table.Column<int>(type: "int", nullable: true),
                    target = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    achievement = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    achievement_percent = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    qunatity_target = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    qunatity_achievement = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    qunatity_achievement_percent = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_salestargetusers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "states",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    state_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    country_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    gst_code = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    updated_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_states", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subcategories",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    ranking = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    subcategory_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    subcategory_image = table.Column<string>(type: "nvarchar(350)", maxLength: 350, nullable: false),
                    sap_code = table.Column<string>(type: "nvarchar(350)", maxLength: 350, nullable: true),
                    category_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    service_category_id = table.Column<string>(type: "nvarchar(191)", maxLength: 191, nullable: true),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    updated_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subcategories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tour_details",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    tourid = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    city_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    visited_date = table.Column<DateTime>(type: "date", nullable: true),
                    visited_cityid = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    last_visited = table.Column<DateTime>(type: "date", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tour_details", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tour_logs",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    tour_programme_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    action = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    performed_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    remark = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tour_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tour_programmes",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    date = table.Column<DateTime>(type: "date", nullable: true),
                    userid = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    town = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    district = table.Column<long>(type: "bigint", nullable: true),
                    objectives = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    status = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tour_programmes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_city_assigns",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    userid = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    reportingid = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    city_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_city_assigns", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_details",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    user_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    date_of_birth = table.Column<DateTime>(type: "datetime2", nullable: true),
                    date_of_joining = table.Column<DateTime>(type: "datetime2", nullable: true),
                    marital_status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true, defaultValue: ""),
                    pan_number = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    aadhar_number = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    emergency_number = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    current_address = table.Column<string>(type: "text", nullable: true),
                    permanent_address = table.Column<string>(type: "text", nullable: true),
                    father_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    father_date_of_birth = table.Column<DateTime>(type: "datetime2", nullable: true),
                    mother_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    mother_date_of_birth = table.Column<DateTime>(type: "datetime2", nullable: true),
                    marriage_anniversary = table.Column<DateTime>(type: "datetime2", nullable: true),
                    spouse_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    spouse_date_of_birth = table.Column<DateTime>(type: "datetime2", nullable: true),
                    children_one = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    children_one_date_of_birth = table.Column<DateTime>(type: "datetime2", nullable: true),
                    children_two = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    children_two_date_of_birth = table.Column<DateTime>(type: "datetime2", nullable: true),
                    children_three = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    children_three_date_of_birth = table.Column<DateTime>(type: "datetime2", nullable: true),
                    children_four = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    children_four_date_of_birth = table.Column<DateTime>(type: "datetime2", nullable: true),
                    children_five = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    children_five_date_of_birth = table.Column<DateTime>(type: "datetime2", nullable: true),
                    account_number = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    bank_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ifsc_code = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    salary = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ctc_annual = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    gross_salary_monthly = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    last_year_increments = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    last_year_increment_percent = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    last_year_increment_value = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    last_promotion = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    pf_number = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    un_number = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    esi_number = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    probation_period = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    date_of_confirmation = table.Column<DateTime>(type: "datetime2", nullable: true),
                    notice_period = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    date_of_leaving = table.Column<DateTime>(type: "datetime2", nullable: true),
                    biometric_code = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    order_mails = table.Column<string>(type: "text", nullable: true),
                    order_mails_type = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    other_education = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    previous_exp = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    current_company_tenture = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    total_exp = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_details", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_education",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    user_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    education_type_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    degree_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    board_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    percentage = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    grade = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_education", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    active = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "Y"),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    first_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    last_name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    mobile = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    email = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    email_verified_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    password = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    password_string = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    remember_token = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    notification_id = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false, defaultValue: ""),
                    device_type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: ""),
                    gender = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: ""),
                    profile_image = table.Column<string>(type: "nvarchar(350)", maxLength: 350, nullable: false, defaultValue: ""),
                    latitude = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: ""),
                    longitude = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: ""),
                    user_code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: ""),
                    location = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false, defaultValue: ""),
                    reportingid = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    region_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    employee_codes = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    branch_id = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    primary_branch_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    branch_show = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    designation_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    department_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    division_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    warehouse_id = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    sales_type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: ""),
                    created_by = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    payroll = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    leave_balance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    compb_off = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    grade = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    blood_group = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    personal_number = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    customerid = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    show_attandance_report = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    earned_leave_balance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    casual_leave_balance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    sick_leave_balance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    date_of_joining = table.Column<DateTime>(type: "datetime2", nullable: true),
                    last_leave_accrual_date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    earned_leave_claim_activated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    claimable_earned_leave_balance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    isDeleted = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_scheme_slabs",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    loyalty_scheme_id = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    tier_name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    value_from = table.Column<decimal>(type: "decimal(15,2)", precision: 15, scale: 2, nullable: false),
                    value_to = table.Column<decimal>(type: "decimal(15,2)", precision: 15, scale: 2, nullable: true),
                    reward_value = table.Column<decimal>(type: "decimal(15,2)", precision: 15, scale: 2, nullable: false),
                    sort_order = table.Column<int>(type: "int", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_scheme_slabs", x => x.id);
                    table.ForeignKey(
                        name: "FK_loyalty_scheme_slabs_loyalty_schemes_loyalty_scheme_id",
                        column: x => x.loyalty_scheme_id,
                        principalTable: "loyalty_schemes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "model_has_permissions",
                columns: table => new
                {
                    permission_id = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    model_type = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    model_id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_has_permissions", x => new { x.permission_id, x.model_id, x.model_type });
                    table.ForeignKey(
                        name: "FK_model_has_permissions_permissions_permission_id",
                        column: x => x.permission_id,
                        principalTable: "permissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "model_has_roles",
                columns: table => new
                {
                    role_id = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    model_type = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    model_id = table.Column<decimal>(type: "decimal(20,0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_has_roles", x => new { x.role_id, x.model_id, x.model_type });
                    table.ForeignKey(
                        name: "FK_model_has_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_branches_branch_name",
                table: "branches",
                column: "branch_name");

            migrationBuilder.CreateIndex(
                name: "IX_cities_city_name",
                table: "cities",
                column: "city_name");

            migrationBuilder.CreateIndex(
                name: "IX_cities_district_id",
                table: "cities",
                column: "district_id");

            migrationBuilder.CreateIndex(
                name: "IX_cities_state_id",
                table: "cities",
                column: "state_id");

            migrationBuilder.CreateIndex(
                name: "IX_countries_country_name",
                table: "countries",
                column: "country_name");

            migrationBuilder.CreateIndex(
                name: "IX_customers_email",
                table: "customers",
                column: "email",
                unique: true,
                filter: "[email] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_customers_mobile",
                table: "customers",
                column: "mobile",
                unique: true,
                filter: "[mobile] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_customers_name",
                table: "customers",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "IX_departments_name",
                table: "departments",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "IX_designations_designation_name",
                table: "designations",
                column: "designation_name");

            migrationBuilder.CreateIndex(
                name: "IX_districts_district_name",
                table: "districts",
                column: "district_name");

            migrationBuilder.CreateIndex(
                name: "IX_districts_state_id",
                table: "districts",
                column: "state_id");

            migrationBuilder.CreateIndex(
                name: "IX_divisions_division_name",
                table: "divisions",
                column: "division_name");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemptions_customer_id",
                table: "loyalty_redemptions",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemptions_loyalty_scheme_id",
                table: "loyalty_redemptions",
                column: "loyalty_scheme_id");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemptions_status",
                table: "loyalty_redemptions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemptions_transaction_no",
                table: "loyalty_redemptions",
                column: "transaction_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemptions_wallet_type",
                table: "loyalty_redemptions",
                column: "wallet_type");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_scheme_slabs_loyalty_scheme_id",
                table: "loyalty_scheme_slabs",
                column: "loyalty_scheme_id");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_schemes_scheme_code",
                table: "loyalty_schemes",
                column: "scheme_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_schemes_scheme_name",
                table: "loyalty_schemes",
                column: "scheme_name");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_schemes_status",
                table: "loyalty_schemes",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_new_invoice_approval_logs_created_by",
                table: "new_invoice_approval_logs",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_new_invoice_approval_logs_new_invoice_id",
                table: "new_invoice_approval_logs",
                column: "new_invoice_id");

            migrationBuilder.CreateIndex(
                name: "IX_new_invoices_approval_status",
                table: "new_invoices",
                column: "approval_status");

            migrationBuilder.CreateIndex(
                name: "IX_new_invoices_created_by",
                table: "new_invoices",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_new_invoices_invoice_number",
                table: "new_invoices",
                column: "invoice_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_new_invoices_loyalty_scheme_id",
                table: "new_invoices",
                column: "loyalty_scheme_id");

            migrationBuilder.CreateIndex(
                name: "IX_new_invoices_secondary_customer_id",
                table: "new_invoices",
                column: "secondary_customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_oauth_access_tokens_user_id",
                table: "oauth_access_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_created_by",
                table: "orders",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_permissions_name_guard_name",
                table: "permissions",
                columns: new[] { "name", "guard_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pincodes_city_id",
                table: "pincodes",
                column: "city_id");

            migrationBuilder.CreateIndex(
                name: "IX_pincodes_pincode",
                table: "pincodes",
                column: "pincode");

            migrationBuilder.CreateIndex(
                name: "IX_roles_name_guard_name",
                table: "roles",
                columns: new[] { "name", "guard_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_salestargetusers_branch_id",
                table: "salestargetusers",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "IX_salestargetusers_user_id",
                table: "salestargetusers",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_states_country_id",
                table: "states",
                column: "country_id");

            migrationBuilder.CreateIndex(
                name: "IX_states_state_name",
                table: "states",
                column: "state_name");

            migrationBuilder.CreateIndex(
                name: "IX_user_city_assigns_city_id",
                table: "user_city_assigns",
                column: "city_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_city_assigns_reportingid",
                table: "user_city_assigns",
                column: "reportingid");

            migrationBuilder.CreateIndex(
                name: "IX_user_city_assigns_userid",
                table: "user_city_assigns",
                column: "userid");

            migrationBuilder.CreateIndex(
                name: "IX_user_details_user_id",
                table: "user_details",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_education_user_id",
                table: "user_education",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email",
                unique: true,
                filter: "[email] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_users_mobile",
                table: "users",
                column: "mobile",
                unique: true,
                filter: "[mobile] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_users_name",
                table: "users",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "IX_users_reportingid",
                table: "users",
                column: "reportingid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attendances");

            migrationBuilder.DropTable(
                name: "beat_schedules");

            migrationBuilder.DropTable(
                name: "beats");

            migrationBuilder.DropTable(
                name: "branches");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "cities");

            migrationBuilder.DropTable(
                name: "comp_off_leaves");

            migrationBuilder.DropTable(
                name: "countries");

            migrationBuilder.DropTable(
                name: "customers");

            migrationBuilder.DropTable(
                name: "departments");

            migrationBuilder.DropTable(
                name: "designations");

            migrationBuilder.DropTable(
                name: "districts");

            migrationBuilder.DropTable(
                name: "divisions");

            migrationBuilder.DropTable(
                name: "expense_logs");

            migrationBuilder.DropTable(
                name: "expenses");

            migrationBuilder.DropTable(
                name: "expenses_types");

            migrationBuilder.DropTable(
                name: "holidays");

            migrationBuilder.DropTable(
                name: "leaves");

            migrationBuilder.DropTable(
                name: "loyalty_redemptions");

            migrationBuilder.DropTable(
                name: "loyalty_scheme_slabs");

            migrationBuilder.DropTable(
                name: "media");

            migrationBuilder.DropTable(
                name: "mobile_user_login_details");

            migrationBuilder.DropTable(
                name: "model_has_permissions");

            migrationBuilder.DropTable(
                name: "model_has_roles");

            migrationBuilder.DropTable(
                name: "new_invoice_approval_logs");

            migrationBuilder.DropTable(
                name: "new_invoices");

            migrationBuilder.DropTable(
                name: "oauth_access_tokens");

            migrationBuilder.DropTable(
                name: "order_details");

            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "pincodes");

            migrationBuilder.DropTable(
                name: "primary_sales");

            migrationBuilder.DropTable(
                name: "product_details");

            migrationBuilder.DropTable(
                name: "products");

            migrationBuilder.DropTable(
                name: "role_has_permissions");

            migrationBuilder.DropTable(
                name: "salestargetusers");

            migrationBuilder.DropTable(
                name: "states");

            migrationBuilder.DropTable(
                name: "subcategories");

            migrationBuilder.DropTable(
                name: "tour_details");

            migrationBuilder.DropTable(
                name: "tour_logs");

            migrationBuilder.DropTable(
                name: "tour_programmes");

            migrationBuilder.DropTable(
                name: "user_city_assigns");

            migrationBuilder.DropTable(
                name: "user_details");

            migrationBuilder.DropTable(
                name: "user_education");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "loyalty_schemes");

            migrationBuilder.DropTable(
                name: "permissions");

            migrationBuilder.DropTable(
                name: "roles");
        }
    }
}
