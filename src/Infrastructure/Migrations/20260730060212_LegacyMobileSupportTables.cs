using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LegacyMobileSupportTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE [addresses] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [active] nvarchar(1) NOT NULL DEFAULT N'Y',
                    [address1] nvarchar(250) NOT NULL DEFAULT N'',
                    [address2] nvarchar(250) NOT NULL DEFAULT N'',
                    [landmark] nvarchar(250) NOT NULL DEFAULT N'',
                    [locality] nvarchar(250) NOT NULL DEFAULT N'',
                    [customer_id] decimal(20,0) NULL,
                    [user_id] decimal(20,0) NULL,
                    [country_id] decimal(20,0) NULL,
                    [state_id] decimal(20,0) NULL,
                    [district_id] decimal(20,0) NULL,
                    [city_id] decimal(20,0) NULL,
                    [pincode_id] decimal(20,0) NULL,
                    [zipcode] nvarchar(250) NULL,
                    [created_by] decimal(20,0) NULL,
                    [model_type] nvarchar(255) NULL,
                    [model_id] nvarchar(255) NULL,
                    [deleted_at] datetime2 NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE INDEX [IX_addresses_customer_id] ON [addresses] ([customer_id]);
                CREATE INDEX [IX_addresses_user_id] ON [addresses] ([user_id]);

                CREATE TABLE [beat_customers] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [active] nvarchar(1) NOT NULL DEFAULT N'Y',
                    [beat_id] decimal(20,0) NULL,
                    [distributor_id] decimal(20,0) NULL,
                    [customer_id] decimal(20,0) NULL,
                    [customer_type] nvarchar(255) NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE INDEX [IX_beat_customers_customer_id] ON [beat_customers] ([customer_id]);

                CREATE TABLE [beat_users] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [active] nvarchar(1) NOT NULL DEFAULT N'Y',
                    [beat_id] decimal(20,0) NULL,
                    [user_id] decimal(20,0) NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE TABLE [check_in] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [active] nvarchar(1) NOT NULL DEFAULT N'Y',
                    [customer_id] decimal(20,0) NULL,
                    [entity_type] nvarchar(255) NULL,
                    [entity_id] decimal(20,0) NULL,
                    [user_id] decimal(20,0) NULL,
                    [checkin_date] date NOT NULL,
                    [checkin_time] time NOT NULL,
                    [checkin_latitude] nvarchar(250) NULL,
                    [checkin_longitude] nvarchar(250) NULL,
                    [checkin_address] nvarchar(250) NULL,
                    [checkout_date] date NULL,
                    [checkout_time] time NULL,
                    [time_interval] time NULL,
                    [checkout_latitude] nvarchar(250) NULL,
                    [checkout_longitude] nvarchar(250) NULL,
                    [checkout_address] nvarchar(250) NULL,
                    [distance] nvarchar(250) NULL,
                    [beatscheduleid] decimal(20,0) NULL,
                    [deleted_at] datetime2 NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE INDEX [IX_check_in_user_date] ON [check_in] ([user_id], [checkin_date]);
                CREATE INDEX [IX_check_in_customer_id] ON [check_in] ([customer_id]);

                CREATE TABLE [check_in_drafts] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [checkin_id] decimal(20,0) NULL,
                    [draft_msg] nvarchar(max) NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE TABLE [customer_details] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [active] nvarchar(1) NOT NULL DEFAULT N'Y',
                    [customer_id] decimal(20,0) NULL,
                    [gstin_no] nvarchar(250) NULL,
                    [pan_no] nvarchar(250) NULL,
                    [aadhar_no] nvarchar(250) NULL,
                    [account_holder] nvarchar(125) NULL,
                    [account_number] nvarchar(125) NULL,
                    [bank_name] nvarchar(125) NULL,
                    [ifsc_code] nvarchar(125) NULL,
                    [otherid_no] nvarchar(250) NULL,
                    [gstin_no_status] int NOT NULL DEFAULT 0,
                    [pan_no_status] int NOT NULL DEFAULT 0,
                    [aadhar_no_status] int NOT NULL DEFAULT 0,
                    [bank_status] int NOT NULL DEFAULT 0,
                    [otherid_no_status] int NOT NULL DEFAULT 0,
                    [status_update_by] decimal(20,0) NULL,
                    [enrollment_date] datetime2 NULL,
                    [approval_date] datetime2 NULL,
                    [shop_image] nvarchar(250) NOT NULL DEFAULT N'',
                    [visiting_card] nvarchar(250) NULL,
                    [grade] nvarchar(250) NOT NULL DEFAULT N'',
                    [visit_status] nvarchar(250) NOT NULL DEFAULT N'',
                    [fcm_token] nvarchar(max) NULL,
                    [deleted_at] datetime2 NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE INDEX [IX_customer_details_customer_id] ON [customer_details] ([customer_id]);

                CREATE TABLE [customer_types] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [active] nvarchar(1) NOT NULL DEFAULT N'Y',
                    [customertype_name] nvarchar(250) NOT NULL,
                    [type_name] nvarchar(250) NOT NULL,
                    [created_by] decimal(20,0) NULL,
                    [updated_by] decimal(20,0) NULL,
                    [deleted_at] datetime2 NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                SET IDENTITY_INSERT [customer_types] ON;
                INSERT INTO [customer_types]
                    ([id], [active], [customertype_name], [type_name], [created_at], [updated_at])
                VALUES
                    (1, N'Y', N'Dealer', N'Dealer', SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (2, N'Y', N'Retailer', N'Retailer', SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (3, N'Y', N'Influencer', N'Influencer', SYSUTCDATETIME(), SYSUTCDATETIME());
                SET IDENTITY_INSERT [customer_types] OFF;

                CREATE TABLE [employee_details] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [active] nvarchar(1) NOT NULL DEFAULT N'Y',
                    [customer_id] decimal(20,0) NULL,
                    [user_id] decimal(20,0) NULL,
                    [created_by] decimal(20,0) NULL,
                    [updated_by] decimal(20,0) NULL,
                    [deleted_at] datetime2 NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE TABLE [field_konnect_app_settings] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [app_version] nvarchar(255) NOT NULL,
                    [app_ios_version] nvarchar(255) NULL,
                    [order_discount_limit] int NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE TABLE [parent_details] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [active] nvarchar(1) NOT NULL DEFAULT N'Y',
                    [customer_id] decimal(20,0) NULL,
                    [parent_id] decimal(20,0) NULL,
                    [created_by] decimal(20,0) NULL,
                    [updated_by] decimal(20,0) NULL,
                    [deleted_at] datetime2 NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE TABLE [payment_details] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [active] nvarchar(1) NOT NULL DEFAULT N'Y',
                    [payment_id] decimal(20,0) NULL,
                    [sales_id] decimal(20,0) NULL,
                    [invoice_no] nvarchar(200) NOT NULL DEFAULT N'',
                    [amount] decimal(19,2) NOT NULL DEFAULT 0,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE TABLE [sales] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [active] nvarchar(1) NOT NULL DEFAULT N'Y',
                    [buyer_id] decimal(20,0) NULL,
                    [seller_id] decimal(20,0) NULL,
                    [order_id] decimal(20,0) NULL,
                    [total_qty] bigint NOT NULL DEFAULT 0,
                    [shipped_qty] bigint NOT NULL DEFAULT 0,
                    [orderno] nvarchar(250) NOT NULL DEFAULT N'',
                    [fiscal_year] nvarchar(50) NOT NULL DEFAULT N'',
                    [sales_no] nvarchar(250) NOT NULL DEFAULT N'',
                    [invoice_no] nvarchar(250) NOT NULL DEFAULT N'',
                    [invoice_date] date NULL,
                    [transport_details] nvarchar(max) NULL,
                    [total_gst] decimal(19,2) NOT NULL DEFAULT 0,
                    [total_discount] decimal(19,2) NULL,
                    [extra_discount] decimal(8,2) NULL,
                    [extra_discount_amount] decimal(19,2) NULL,
                    [sub_total] decimal(19,2) NOT NULL DEFAULT 0,
                    [grand_total] decimal(19,2) NOT NULL DEFAULT 0,
                    [paid_amount] decimal(19,2) NOT NULL DEFAULT 0,
                    [description] nvarchar(400) NOT NULL DEFAULT N'',
                    [status_id] decimal(20,0) NULL,
                    [created_by] decimal(20,0) NULL,
                    [updated_by] decimal(20,0) NULL,
                    [transport_name] nvarchar(200) NULL,
                    [lr_no] nvarchar(125) NULL,
                    [dispatch_date] date NULL,
                    [deleted_at] datetime2 NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE TABLE [sales_targets] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [active] nvarchar(1) NOT NULL DEFAULT N'Y',
                    [userid] decimal(20,0) NULL,
                    [startdate] datetime2 NULL,
                    [enddate] datetime2 NULL,
                    [amount] decimal(19,2) NOT NULL DEFAULT 0,
                    [achievement] decimal(19,2) NOT NULL DEFAULT 0,
                    [created_by] decimal(20,0) NULL,
                    [updated_by] decimal(20,0) NULL,
                    [deleted_at] datetime2 NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE TABLE [tasks] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [active] nvarchar(1) NOT NULL DEFAULT N'Y',
                    [user_id] decimal(20,0) NULL,
                    [title] nvarchar(300) NOT NULL DEFAULT N'',
                    [descriptions] nvarchar(255) NOT NULL DEFAULT N'',
                    [task_department_id] decimal(20,0) NULL,
                    [task_type] nvarchar(50) NULL,
                    [task_project_id] decimal(20,0) NULL,
                    [task_priority_id] decimal(20,0) NULL,
                    [lead_id] decimal(20,0) NULL,
                    [due_datetime] datetime2 NULL,
                    [datetime] datetime2 NULL,
                    [reminder] datetime2 NULL,
                    [open_datetime] datetime2 NULL,
                    [inprogress_datetime] datetime2 NULL,
                    [reopen_datetime] datetime2 NULL,
                    [completed_at] datetime2 NULL,
                    [completed] bit NOT NULL DEFAULT 0,
                    [is_done] bit NOT NULL DEFAULT 0,
                    [remark] nvarchar(1000) NOT NULL DEFAULT N'',
                    [customer_id] decimal(20,0) NULL,
                    [status_id] decimal(20,0) NULL,
                    [task_status] nvarchar(50) NOT NULL DEFAULT N'Pending',
                    [created_by] decimal(20,0) NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE TABLE [user_activities] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [active] nvarchar(1) NOT NULL DEFAULT N'Y',
                    [userid] decimal(20,0) NOT NULL,
                    [customerid] decimal(20,0) NULL,
                    [latitude] nvarchar(50) NULL,
                    [longitude] nvarchar(50) NULL,
                    [time] datetime2 NULL,
                    [address] nvarchar(450) NOT NULL DEFAULT N'',
                    [description] nvarchar(450) NOT NULL DEFAULT N'',
                    [type] nvarchar(50) NOT NULL DEFAULT N'',
                    [deleted_at] datetime2 NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE TABLE [user_live_locations] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [active] nvarchar(1) NOT NULL DEFAULT N'Y',
                    [userid] decimal(20,0) NOT NULL,
                    [latitude] nvarchar(50) NULL,
                    [longitude] nvarchar(50) NULL,
                    [time] datetime2 NULL,
                    [address] nvarchar(450) NULL,
                    [deleted_at] datetime2 NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE TABLE [visit_reports] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [checkin_id] decimal(20,0) NULL,
                    [user_id] decimal(20,0) NULL,
                    [customer_id] decimal(20,0) NULL,
                    [visit_type_id] decimal(20,0) NULL,
                    [report_title] nvarchar(200) NOT NULL DEFAULT N'',
                    [description] nvarchar(450) NOT NULL DEFAULT N'',
                    [visit_image] nvarchar(450) NOT NULL DEFAULT N'',
                    [next_visit] datetime2 NULL,
                    [status_id] decimal(20,0) NULL,
                    [created_by] decimal(20,0) NULL,
                    [deleted_at] datetime2 NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE TABLE [visit_types] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [active] nvarchar(1) NOT NULL DEFAULT N'Y',
                    [type_name] nvarchar(250) NOT NULL,
                    [created_by] decimal(20,0) NULL,
                    [deleted_at] datetime2 NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );

                CREATE TABLE [wallets] (
                    [id] decimal(20,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [active] nvarchar(1) NOT NULL DEFAULT N'Y',
                    [customer_id] decimal(20,0) NOT NULL,
                    [scheme_id] decimal(20,0) NULL,
                    [schemedetail_id] decimal(20,0) NULL,
                    [points] bigint NOT NULL DEFAULT 0,
                    [point_type] nvarchar(20) NOT NULL DEFAULT N'',
                    [invoice_amount] decimal(19,2) NOT NULL DEFAULT 0,
                    [invoice_no] nvarchar(200) NOT NULL DEFAULT N'',
                    [coupon_code] nvarchar(250) NOT NULL DEFAULT N'',
                    [invoice_date] date NULL,
                    [transaction_at] datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    [transaction_type] nvarchar(20) NOT NULL DEFAULT N'',
                    [sales_id] decimal(20,0) NULL,
                    [status_id] decimal(20,0) NULL,
                    [checkinid] decimal(20,0) NULL,
                    [quantity] bigint NOT NULL DEFAULT 0,
                    [userid] decimal(20,0) NULL,
                    [deleted_at] datetime2 NULL,
                    [created_at] datetime2 NULL,
                    [updated_at] datetime2 NULL
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS [wallets];
                DROP TABLE IF EXISTS [visit_types];
                DROP TABLE IF EXISTS [visit_reports];
                DROP TABLE IF EXISTS [user_live_locations];
                DROP TABLE IF EXISTS [user_activities];
                DROP TABLE IF EXISTS [tasks];
                DROP TABLE IF EXISTS [sales_targets];
                DROP TABLE IF EXISTS [sales];
                DROP TABLE IF EXISTS [payment_details];
                DROP TABLE IF EXISTS [parent_details];
                DROP TABLE IF EXISTS [field_konnect_app_settings];
                DROP TABLE IF EXISTS [employee_details];
                DROP TABLE IF EXISTS [customer_types];
                DROP TABLE IF EXISTS [customer_details];
                DROP TABLE IF EXISTS [check_in_drafts];
                DROP TABLE IF EXISTS [check_in];
                DROP TABLE IF EXISTS [beat_users];
                DROP TABLE IF EXISTS [beat_customers];
                DROP TABLE IF EXISTS [addresses];
                """);
        }
    }
}
