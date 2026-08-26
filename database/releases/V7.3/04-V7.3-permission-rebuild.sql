-- FieldKonnect V7.3 - complete permission rebuild in pure SQL.
--
-- WHAT THIS IS
-- Everything the API would do on a bootstrap-enabled start, done here instead:
--   1. adds the seven catalog columns to the permissions table
--   2. records the migration so the API never retries it
--   3. syncs the permissions table to the V7.3 catalog - renames the rows that
--      survive (keeping their id, so every role keeps what it was granted),
--      merges duplicates, creates the newly split actions, grants them to the
--      roles that could already perform them, and deletes the rows the CRM
--      does not enforce
--   4. gives superadmin every permission and puts all roles on the users guard
--   5. verifies the result and reports what changed
--
-- Generated from src/Domain/Constants/PermissionCatalog.cs - 230 permissions.
-- No seeder and no API migration are needed. SKIP_DB_BOOTSTRAP stays true.
--
-- WHAT IT TOUCHES
-- permissions, role_has_permissions, model_has_permissions, roles,
-- __EFMigrationsHistory. No business data: no customer, order, visit, expense
-- or invoice table is referenced anywhere below.
--
-- It runs in ONE transaction. Any error rolls the whole thing back, so the
-- database is either fully migrated or exactly as it was.
-- It is safe to run twice: a second run finds everything in place.
--
-- TAKE A FULL DATABASE BACKUP FIRST, then run:
--   sqlcmd -S <server> -U <user> -P <password> -d ksb_pr -i 04-V7.3-permission-rebuild.sql
--
-- Restart the API (IIS app pool recycle) after this completes.

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

PRINT '== FieldKonnect V7.3 permission rebuild ==';
GO

-- ---------------------------------------------------------------------------
-- STEP 1. The seven catalog columns.
-- ---------------------------------------------------------------------------
IF COL_LENGTH('permissions','label')        IS NULL ALTER TABLE permissions ADD [label]        NVARCHAR(255) NULL;
IF COL_LENGTH('permissions','group_key')    IS NULL ALTER TABLE permissions ADD [group_key]    NVARCHAR(64)  NULL;
IF COL_LENGTH('permissions','group_label')  IS NULL ALTER TABLE permissions ADD [group_label]  NVARCHAR(255) NULL;
IF COL_LENGTH('permissions','module_key')   IS NULL ALTER TABLE permissions ADD [module_key]   NVARCHAR(64)  NULL;
IF COL_LENGTH('permissions','module_label') IS NULL ALTER TABLE permissions ADD [module_label] NVARCHAR(255) NULL;
IF COL_LENGTH('permissions','action_key')   IS NULL ALTER TABLE permissions ADD [action_key]   NVARCHAR(64)  NULL;
IF COL_LENGTH('permissions','sort_order')   IS NULL ALTER TABLE permissions ADD [sort_order]   INT NOT NULL CONSTRAINT DF_permissions_sort_order DEFAULT 0;
GO

-- STEP 2. Record the migration so the API does not try to add the columns again.
IF OBJECT_ID('__EFMigrationsHistory','U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM __EFMigrationsHistory WHERE MigrationId = N'20260825103753_AddPermissionCatalogMetadata')
    INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
    VALUES (N'20260825103753_AddPermissionCatalogMetadata', N'8.0.13');
GO

-- ---------------------------------------------------------------------------
-- STEP 3. The catalog itself.
-- ---------------------------------------------------------------------------
IF OBJECT_ID('tempdb..#catalog') IS NOT NULL DROP TABLE #catalog;
IF OBJECT_ID('tempdb..#legacy')  IS NOT NULL DROP TABLE #legacy;
IF OBJECT_ID('tempdb..#inherit') IS NOT NULL DROP TABLE #inherit;

CREATE TABLE #catalog (
    ordinal      INT           NOT NULL PRIMARY KEY,
    name         NVARCHAR(255) NOT NULL UNIQUE,
    label        NVARCHAR(255) NOT NULL,
    module_key   NVARCHAR(64)  NOT NULL,
    module_label NVARCHAR(255) NOT NULL,
    group_key    NVARCHAR(64)  NOT NULL,
    group_label  NVARCHAR(255) NOT NULL,
    action_key   NVARCHAR(64)  NOT NULL,
    sort_order   INT           NOT NULL);

CREATE TABLE #legacy  (name NVARCHAR(255) NOT NULL, legacy_name NVARCHAR(255) NOT NULL, seq INT NOT NULL);
CREATE TABLE #inherit (name NVARCHAR(255) NOT NULL, source_name NVARCHAR(255) NOT NULL);
GO

-- The 230 permissions the CRM enforces.
INSERT INTO #catalog (ordinal,name,label,module_key,module_label,group_key,group_label,action_key,sort_order) VALUES
(0,N'dashboard.view',N'View Dashboard',N'dashboard',N'Dashboard',N'dashboard',N'Dashboard',N'view',10),
(1,N'dashboard.secondary_sales',N'Secondary Sales Tab',N'dashboard',N'Dashboard',N'dashboard',N'Dashboard',N'secondary_sales',20),
(2,N'dashboard.loyalty',N'Loyalty Tab',N'dashboard',N'Dashboard',N'dashboard',N'Dashboard',N'loyalty',30),
(3,N'dashboard.activity',N'Activity Tab',N'dashboard',N'Dashboard',N'dashboard',N'Dashboard',N'activity',40),
(4,N'customer.view',N'View Listing',N'customer',N'Customers',N'customers',N'Customers Management',N'view',50),
(5,N'customer.detail',N'View Detail',N'customer',N'Customers',N'customers',N'Customers Management',N'detail',60),
(6,N'customer.create',N'Create',N'customer',N'Customers',N'customers',N'Customers Management',N'create',70),
(7,N'customer.edit',N'Edit',N'customer',N'Customers',N'customers',N'Customers Management',N'edit',80),
(8,N'customer.delete',N'Delete',N'customer',N'Customers',N'customers',N'Customers Management',N'delete',90),
(9,N'customer.active',N'Activate / Deactivate',N'customer',N'Customers',N'customers',N'Customers Management',N'active',100),
(10,N'customer.export',N'Export',N'customer',N'Customers',N'customers',N'Customers Management',N'export',110),
(11,N'customer.import',N'Import',N'customer',N'Customers',N'customers',N'Customers Management',N'import',120),
(12,N'customer.template',N'Download Template',N'customer',N'Customers',N'customers',N'Customers Management',N'template',130),
(13,N'customer.approve',N'Approve Retailer',N'customer',N'Customers',N'customers',N'Customers Management',N'approve',140),
(14,N'customer.reject',N'Reject Retailer',N'customer',N'Customers',N'customers',N'Customers Management',N'reject',150),
(15,N'customer.pending',N'Mark Retailer Pending',N'customer',N'Customers',N'customers',N'Customers Management',N'pending',160),
(16,N'customer.kyc_review',N'Approve / Reject KYC',N'customer',N'Customers',N'customers',N'Customers Management',N'kyc_review',170),
(17,N'country.view',N'View Listing',N'country',N'Country',N'address',N'Address Management',N'view',180),
(18,N'country.create',N'Create',N'country',N'Country',N'address',N'Address Management',N'create',190),
(19,N'country.edit',N'Edit',N'country',N'Country',N'address',N'Address Management',N'edit',200),
(20,N'country.delete',N'Delete',N'country',N'Country',N'address',N'Address Management',N'delete',210),
(21,N'country.active',N'Activate / Deactivate',N'country',N'Country',N'address',N'Address Management',N'active',220),
(22,N'country.export',N'Export',N'country',N'Country',N'address',N'Address Management',N'export',230),
(23,N'country.import',N'Import',N'country',N'Country',N'address',N'Address Management',N'import',240),
(24,N'country.template',N'Download Template',N'country',N'Country',N'address',N'Address Management',N'template',250),
(25,N'state.view',N'View Listing',N'state',N'State',N'address',N'Address Management',N'view',260),
(26,N'state.create',N'Create',N'state',N'State',N'address',N'Address Management',N'create',270),
(27,N'state.edit',N'Edit',N'state',N'State',N'address',N'Address Management',N'edit',280),
(28,N'state.delete',N'Delete',N'state',N'State',N'address',N'Address Management',N'delete',290),
(29,N'state.active',N'Activate / Deactivate',N'state',N'State',N'address',N'Address Management',N'active',300),
(30,N'state.export',N'Export',N'state',N'State',N'address',N'Address Management',N'export',310),
(31,N'state.import',N'Import',N'state',N'State',N'address',N'Address Management',N'import',320),
(32,N'state.template',N'Download Template',N'state',N'State',N'address',N'Address Management',N'template',330),
(33,N'district.view',N'View Listing',N'district',N'District',N'address',N'Address Management',N'view',340),
(34,N'district.create',N'Create',N'district',N'District',N'address',N'Address Management',N'create',350),
(35,N'district.edit',N'Edit',N'district',N'District',N'address',N'Address Management',N'edit',360),
(36,N'district.delete',N'Delete',N'district',N'District',N'address',N'Address Management',N'delete',370),
(37,N'district.active',N'Activate / Deactivate',N'district',N'District',N'address',N'Address Management',N'active',380),
(38,N'district.export',N'Export',N'district',N'District',N'address',N'Address Management',N'export',390),
(39,N'district.import',N'Import',N'district',N'District',N'address',N'Address Management',N'import',400);
INSERT INTO #catalog (ordinal,name,label,module_key,module_label,group_key,group_label,action_key,sort_order) VALUES
(40,N'district.template',N'Download Template',N'district',N'District',N'address',N'Address Management',N'template',410),
(41,N'city.view',N'View Listing',N'city',N'City',N'address',N'Address Management',N'view',420),
(42,N'city.create',N'Create',N'city',N'City',N'address',N'Address Management',N'create',430),
(43,N'city.edit',N'Edit',N'city',N'City',N'address',N'Address Management',N'edit',440),
(44,N'city.delete',N'Delete',N'city',N'City',N'address',N'Address Management',N'delete',450),
(45,N'city.active',N'Activate / Deactivate',N'city',N'City',N'address',N'Address Management',N'active',460),
(46,N'city.export',N'Export',N'city',N'City',N'address',N'Address Management',N'export',470),
(47,N'city.import',N'Import',N'city',N'City',N'address',N'Address Management',N'import',480),
(48,N'city.template',N'Download Template',N'city',N'City',N'address',N'Address Management',N'template',490),
(49,N'pincode.view',N'View Listing',N'pincode',N'Pincode',N'address',N'Address Management',N'view',500),
(50,N'pincode.create',N'Create',N'pincode',N'Pincode',N'address',N'Address Management',N'create',510),
(51,N'pincode.edit',N'Edit',N'pincode',N'Pincode',N'address',N'Address Management',N'edit',520),
(52,N'pincode.delete',N'Delete',N'pincode',N'Pincode',N'address',N'Address Management',N'delete',530),
(53,N'pincode.active',N'Activate / Deactivate',N'pincode',N'Pincode',N'address',N'Address Management',N'active',540),
(54,N'pincode.export',N'Export',N'pincode',N'Pincode',N'address',N'Address Management',N'export',550),
(55,N'pincode.import',N'Import',N'pincode',N'Pincode',N'address',N'Address Management',N'import',560),
(56,N'pincode.template',N'Download Template',N'pincode',N'Pincode',N'address',N'Address Management',N'template',570),
(57,N'city_assignment.view',N'View Listing',N'city_assignment',N'City Assigned',N'address',N'Address Management',N'view',580),
(58,N'city_assignment.create',N'Assign City',N'city_assignment',N'City Assigned',N'address',N'Address Management',N'create',590),
(59,N'city_assignment.delete',N'Remove Assignment',N'city_assignment',N'City Assigned',N'address',N'Address Management',N'delete',600),
(60,N'city_assignment.export',N'Export',N'city_assignment',N'City Assigned',N'address',N'Address Management',N'export',610),
(61,N'city_assignment.import',N'Import',N'city_assignment',N'City Assigned',N'address',N'Address Management',N'import',620),
(62,N'city_assignment.template',N'Download Template',N'city_assignment',N'City Assigned',N'address',N'Address Management',N'template',630),
(63,N'segment.view',N'View Listing',N'segment',N'Segment',N'products',N'Product Management',N'view',640),
(64,N'segment.create',N'Create',N'segment',N'Segment',N'products',N'Product Management',N'create',650),
(65,N'segment.edit',N'Edit',N'segment',N'Segment',N'products',N'Product Management',N'edit',660),
(66,N'segment.delete',N'Delete',N'segment',N'Segment',N'products',N'Product Management',N'delete',670),
(67,N'segment.active',N'Activate / Deactivate',N'segment',N'Segment',N'products',N'Product Management',N'active',680),
(68,N'segment.export',N'Export',N'segment',N'Segment',N'products',N'Product Management',N'export',690),
(69,N'segment.import',N'Import',N'segment',N'Segment',N'products',N'Product Management',N'import',700),
(70,N'segment.template',N'Download Template',N'segment',N'Segment',N'products',N'Product Management',N'template',710),
(71,N'family.view',N'View Listing',N'family',N'Family',N'products',N'Product Management',N'view',720),
(72,N'family.create',N'Create',N'family',N'Family',N'products',N'Product Management',N'create',730),
(73,N'family.edit',N'Edit',N'family',N'Family',N'products',N'Product Management',N'edit',740),
(74,N'family.delete',N'Delete',N'family',N'Family',N'products',N'Product Management',N'delete',750),
(75,N'family.active',N'Activate / Deactivate',N'family',N'Family',N'products',N'Product Management',N'active',760),
(76,N'family.export',N'Export',N'family',N'Family',N'products',N'Product Management',N'export',770),
(77,N'family.import',N'Import',N'family',N'Family',N'products',N'Product Management',N'import',780),
(78,N'family.template',N'Download Template',N'family',N'Family',N'products',N'Product Management',N'template',790),
(79,N'product.view',N'View Listing',N'product',N'Products',N'products',N'Product Management',N'view',800);
INSERT INTO #catalog (ordinal,name,label,module_key,module_label,group_key,group_label,action_key,sort_order) VALUES
(80,N'product.create',N'Create',N'product',N'Products',N'products',N'Product Management',N'create',810),
(81,N'product.edit',N'Edit',N'product',N'Products',N'products',N'Product Management',N'edit',820),
(82,N'product.delete',N'Delete',N'product',N'Products',N'products',N'Product Management',N'delete',830),
(83,N'product.active',N'Activate / Deactivate',N'product',N'Products',N'products',N'Product Management',N'active',840),
(84,N'product.export',N'Export',N'product',N'Products',N'products',N'Product Management',N'export',850),
(85,N'product.import',N'Import',N'product',N'Products',N'products',N'Product Management',N'import',860),
(86,N'product.template',N'Download Template',N'product',N'Products',N'products',N'Product Management',N'template',870),
(87,N'attendance.view',N'View Listing',N'attendance',N'Attendance Details',N'hr',N'HR Management',N'view',880),
(88,N'attendance.punch_in',N'Punch In',N'attendance',N'Attendance Details',N'hr',N'HR Management',N'punch_in',890),
(89,N'attendance.punch_out',N'Punch Out',N'attendance',N'Attendance Details',N'hr',N'HR Management',N'punch_out',900),
(90,N'attendance.approve',N'Approve',N'attendance',N'Attendance Details',N'hr',N'HR Management',N'approve',910),
(91,N'attendance.reject',N'Reject',N'attendance',N'Attendance Details',N'hr',N'HR Management',N'reject',920),
(92,N'attendance.delete',N'Delete',N'attendance',N'Attendance Details',N'hr',N'HR Management',N'delete',930),
(93,N'attendance.export',N'Export',N'attendance',N'Attendance Details',N'hr',N'HR Management',N'export',940),
(94,N'attendance_summary.view',N'View Listing',N'attendance_summary',N'Attendance Summary',N'hr',N'HR Management',N'view',950),
(95,N'attendance_summary.export',N'Export',N'attendance_summary',N'Attendance Summary',N'hr',N'HR Management',N'export',960),
(96,N'holiday.view',N'View Listing',N'holiday',N'Holidays',N'hr',N'HR Management',N'view',970),
(97,N'holiday.create',N'Create',N'holiday',N'Holidays',N'hr',N'HR Management',N'create',980),
(98,N'holiday.edit',N'Edit',N'holiday',N'Holidays',N'hr',N'HR Management',N'edit',990),
(99,N'holiday.delete',N'Delete',N'holiday',N'Holidays',N'hr',N'HR Management',N'delete',1000),
(100,N'holiday.export',N'Export',N'holiday',N'Holidays',N'hr',N'HR Management',N'export',1010),
(101,N'leave.view',N'View Listing',N'leave',N'Leaves',N'hr',N'HR Management',N'view',1020),
(102,N'leave.create',N'Create',N'leave',N'Leaves',N'hr',N'HR Management',N'create',1030),
(103,N'leave.delete',N'Delete',N'leave',N'Leaves',N'hr',N'HR Management',N'delete',1040),
(104,N'leave.approve',N'Approve',N'leave',N'Leaves',N'hr',N'HR Management',N'approve',1050),
(105,N'leave.reject',N'Reject',N'leave',N'Leaves',N'hr',N'HR Management',N'reject',1060),
(106,N'leave.comp_off',N'Add Comp Off',N'leave',N'Leaves',N'hr',N'HR Management',N'comp_off',1070),
(107,N'leave.export',N'Export',N'leave',N'Leaves',N'hr',N'HR Management',N'export',1080),
(108,N'tour.view',N'View Listing',N'tour',N'Tours',N'hr',N'HR Management',N'view',1090),
(109,N'tour.create',N'Create',N'tour',N'Tours',N'hr',N'HR Management',N'create',1100),
(110,N'tour.edit',N'Edit',N'tour',N'Tours',N'hr',N'HR Management',N'edit',1110),
(111,N'tour.delete',N'Delete',N'tour',N'Tours',N'hr',N'HR Management',N'delete',1120),
(112,N'tour.status',N'Approve / Reject',N'tour',N'Tours',N'hr',N'HR Management',N'status',1130),
(113,N'tour.export',N'Export',N'tour',N'Tours',N'hr',N'HR Management',N'export',1140),
(114,N'tour.import',N'Import',N'tour',N'Tours',N'hr',N'HR Management',N'import',1150),
(115,N'tour.template',N'Download Template',N'tour',N'Tours',N'hr',N'HR Management',N'template',1160),
(116,N'branch.view',N'View Listing',N'branch',N'Branch',N'hr',N'HR Management',N'view',1170),
(117,N'branch.create',N'Create',N'branch',N'Branch',N'hr',N'HR Management',N'create',1180),
(118,N'branch.edit',N'Edit',N'branch',N'Branch',N'hr',N'HR Management',N'edit',1190),
(119,N'branch.delete',N'Delete',N'branch',N'Branch',N'hr',N'HR Management',N'delete',1200);
INSERT INTO #catalog (ordinal,name,label,module_key,module_label,group_key,group_label,action_key,sort_order) VALUES
(120,N'branch.active',N'Activate / Deactivate',N'branch',N'Branch',N'hr',N'HR Management',N'active',1210),
(121,N'branch.export',N'Export',N'branch',N'Branch',N'hr',N'HR Management',N'export',1220),
(122,N'zone.view',N'View Listing',N'zone',N'Zone',N'hr',N'HR Management',N'view',1230),
(123,N'zone.create',N'Create',N'zone',N'Zone',N'hr',N'HR Management',N'create',1240),
(124,N'zone.edit',N'Edit',N'zone',N'Zone',N'hr',N'HR Management',N'edit',1250),
(125,N'zone.delete',N'Delete',N'zone',N'Zone',N'hr',N'HR Management',N'delete',1260),
(126,N'zone.active',N'Activate / Deactivate',N'zone',N'Zone',N'hr',N'HR Management',N'active',1270),
(127,N'zone.export',N'Export',N'zone',N'Zone',N'hr',N'HR Management',N'export',1280),
(128,N'designation.view',N'View Listing',N'designation',N'Designation',N'hr',N'HR Management',N'view',1290),
(129,N'designation.create',N'Create',N'designation',N'Designation',N'hr',N'HR Management',N'create',1300),
(130,N'designation.edit',N'Edit',N'designation',N'Designation',N'hr',N'HR Management',N'edit',1310),
(131,N'designation.delete',N'Delete',N'designation',N'Designation',N'hr',N'HR Management',N'delete',1320),
(132,N'designation.active',N'Activate / Deactivate',N'designation',N'Designation',N'hr',N'HR Management',N'active',1330),
(133,N'designation.export',N'Export',N'designation',N'Designation',N'hr',N'HR Management',N'export',1340),
(134,N'department.view',N'View Listing',N'department',N'Departments',N'hr',N'HR Management',N'view',1350),
(135,N'department.create',N'Create',N'department',N'Departments',N'hr',N'HR Management',N'create',1360),
(136,N'department.edit',N'Edit',N'department',N'Departments',N'hr',N'HR Management',N'edit',1370),
(137,N'department.delete',N'Delete',N'department',N'Departments',N'hr',N'HR Management',N'delete',1380),
(138,N'department.active',N'Activate / Deactivate',N'department',N'Departments',N'hr',N'HR Management',N'active',1390),
(139,N'department.export',N'Export',N'department',N'Departments',N'hr',N'HR Management',N'export',1400),
(140,N'user.view',N'View Listing',N'user',N'User Details',N'users',N'User Management',N'view',1410),
(141,N'user.create',N'Create',N'user',N'User Details',N'users',N'User Management',N'create',1420),
(142,N'user.edit',N'Edit',N'user',N'User Details',N'users',N'User Management',N'edit',1430),
(143,N'user.delete',N'Delete',N'user',N'User Details',N'users',N'User Management',N'delete',1440),
(144,N'user.active',N'Activate / Deactivate',N'user',N'User Details',N'users',N'User Management',N'active',1450),
(145,N'user.export',N'Export',N'user',N'User Details',N'users',N'User Management',N'export',1460),
(146,N'user.import',N'Import',N'user',N'User Details',N'users',N'User Management',N'import',1470),
(147,N'user.template',N'Download Template',N'user',N'User Details',N'users',N'User Management',N'template',1480),
(148,N'user_app.view',N'View Listing',N'user_app',N'User App Details',N'users',N'User Management',N'view',1490),
(149,N'user_app.force_logout',N'Force Logout',N'user_app',N'User App Details',N'users',N'User Management',N'force_logout',1500),
(150,N'user_app.reset_device',N'Remove Device UUID',N'user_app',N'User App Details',N'users',N'User Management',N'reset_device',1510),
(151,N'user_activity.view',N'View Live Activity',N'user_activity',N'User Live Activity',N'users',N'User Management',N'view',1520),
(152,N'user_target.view',N'View Listing',N'user_target',N'User Target',N'users',N'User Management',N'view',1530),
(153,N'user_target.create',N'Create',N'user_target',N'User Target',N'users',N'User Management',N'create',1540),
(154,N'user_target.edit',N'Edit',N'user_target',N'User Target',N'users',N'User Management',N'edit',1550),
(155,N'user_target.delete',N'Delete',N'user_target',N'User Target',N'users',N'User Management',N'delete',1560),
(156,N'user_target.export',N'Export',N'user_target',N'User Target',N'users',N'User Management',N'export',1570),
(157,N'user_target.import',N'Import',N'user_target',N'User Target',N'users',N'User Management',N'import',1580),
(158,N'user_target.template',N'Download Template',N'user_target',N'User Target',N'users',N'User Management',N'template',1590),
(159,N'expense_type.view',N'View Listing',N'expense_type',N'Expenses Type',N'accounts',N'Account Management',N'view',1600);
INSERT INTO #catalog (ordinal,name,label,module_key,module_label,group_key,group_label,action_key,sort_order) VALUES
(160,N'expense_type.create',N'Create',N'expense_type',N'Expenses Type',N'accounts',N'Account Management',N'create',1610),
(161,N'expense_type.edit',N'Edit',N'expense_type',N'Expenses Type',N'accounts',N'Account Management',N'edit',1620),
(162,N'expense_type.delete',N'Delete',N'expense_type',N'Expenses Type',N'accounts',N'Account Management',N'delete',1630),
(163,N'expense_type.active',N'Activate / Deactivate',N'expense_type',N'Expenses Type',N'accounts',N'Account Management',N'active',1640),
(164,N'expense.view',N'View Listing',N'expense',N'Expense',N'accounts',N'Account Management',N'view',1650),
(165,N'expense.create',N'Create',N'expense',N'Expense',N'accounts',N'Account Management',N'create',1660),
(166,N'expense.edit',N'Edit',N'expense',N'Expense',N'accounts',N'Account Management',N'edit',1670),
(167,N'expense.delete',N'Delete',N'expense',N'Expense',N'accounts',N'Account Management',N'delete',1680),
(168,N'expense.check',N'Check as Reporting Manager',N'expense',N'Expense',N'accounts',N'Account Management',N'check',1690),
(169,N'expense.approve',N'Approve / Reject',N'expense',N'Expense',N'accounts',N'Account Management',N'approve',1700),
(170,N'order.view',N'View Listing',N'order',N'Orders',N'orders',N'Order Management',N'view',1710),
(171,N'order.detail',N'View Detail',N'order',N'Orders',N'orders',N'Order Management',N'detail',1720),
(172,N'order.create',N'Create',N'order',N'Orders',N'orders',N'Order Management',N'create',1730),
(173,N'order.edit',N'Edit',N'order',N'Orders',N'orders',N'Order Management',N'edit',1740),
(174,N'order.delete',N'Delete',N'order',N'Orders',N'orders',N'Order Management',N'delete',1750),
(175,N'order.active',N'Activate / Deactivate',N'order',N'Orders',N'orders',N'Order Management',N'active',1760),
(176,N'order.export',N'Export',N'order',N'Orders',N'orders',N'Order Management',N'export',1770),
(177,N'order.dispatch',N'Dispatch',N'order',N'Orders',N'orders',N'Order Management',N'dispatch',1780),
(178,N'order_dispatch.view',N'View Listing',N'order_dispatch',N'Order Dispatch',N'orders',N'Order Management',N'view',1790),
(179,N'order_dispatch.detail',N'View Detail',N'order_dispatch',N'Order Dispatch',N'orders',N'Order Management',N'detail',1800),
(180,N'invoice_transaction.view',N'View Listing',N'invoice_transaction',N'Invoices Transaction',N'loyalty',N'Loyalty Management',N'view',1810),
(181,N'invoice_transaction.detail',N'View Detail',N'invoice_transaction',N'Invoices Transaction',N'loyalty',N'Loyalty Management',N'detail',1815),
(182,N'invoice_transaction.create',N'Create',N'invoice_transaction',N'Invoices Transaction',N'loyalty',N'Loyalty Management',N'create',1820),
(183,N'invoice_transaction.edit',N'Edit',N'invoice_transaction',N'Invoices Transaction',N'loyalty',N'Loyalty Management',N'edit',1830),
(184,N'invoice_transaction.delete',N'Delete',N'invoice_transaction',N'Invoices Transaction',N'loyalty',N'Loyalty Management',N'delete',1840),
(185,N'invoice_transaction.export',N'Export',N'invoice_transaction',N'Invoices Transaction',N'loyalty',N'Loyalty Management',N'export',1850),
(186,N'invoice_transaction.approve_ss',N'Approve by SS',N'invoice_transaction',N'Invoices Transaction',N'loyalty',N'Loyalty Management',N'approve_ss',1860),
(187,N'invoice_transaction.approve_sales',N'Approve by Sales',N'invoice_transaction',N'Invoices Transaction',N'loyalty',N'Loyalty Management',N'approve_sales',1870),
(188,N'invoice_transaction.approve_ho',N'Approve by HO',N'invoice_transaction',N'Invoices Transaction',N'loyalty',N'Loyalty Management',N'approve_ho',1880),
(189,N'invoice_transaction.hold',N'Hold',N'invoice_transaction',N'Invoices Transaction',N'loyalty',N'Loyalty Management',N'hold',1890),
(190,N'invoice_transaction.reject',N'Reject',N'invoice_transaction',N'Invoices Transaction',N'loyalty',N'Loyalty Management',N'reject',1900),
(191,N'scheme.view',N'View Listing',N'scheme',N'Scheme Creation',N'loyalty',N'Loyalty Management',N'view',1910),
(192,N'scheme.detail',N'View Detail',N'scheme',N'Scheme Creation',N'loyalty',N'Loyalty Management',N'detail',1920),
(193,N'scheme.create',N'Create',N'scheme',N'Scheme Creation',N'loyalty',N'Loyalty Management',N'create',1930),
(194,N'scheme.edit',N'Edit',N'scheme',N'Scheme Creation',N'loyalty',N'Loyalty Management',N'edit',1940),
(195,N'scheme.delete',N'Delete',N'scheme',N'Scheme Creation',N'loyalty',N'Loyalty Management',N'delete',1950),
(196,N'scheme.draft',N'Send to Draft',N'scheme',N'Scheme Creation',N'loyalty',N'Loyalty Management',N'draft',1960),
(197,N'scheme.submit',N'Submit to Next Level',N'scheme',N'Scheme Creation',N'loyalty',N'Loyalty Management',N'submit',1970),
(198,N'scheme.approve',N'Approve',N'scheme',N'Scheme Creation',N'loyalty',N'Loyalty Management',N'approve',1980),
(199,N'scheme.reject',N'Reject',N'scheme',N'Scheme Creation',N'loyalty',N'Loyalty Management',N'reject',1990);
INSERT INTO #catalog (ordinal,name,label,module_key,module_label,group_key,group_label,action_key,sort_order) VALUES
(200,N'scheme.publish',N'Publish',N'scheme',N'Scheme Creation',N'loyalty',N'Loyalty Management',N'publish',2000),
(201,N'redemption.view',N'View Listing',N'redemption',N'Redemption',N'loyalty',N'Loyalty Management',N'view',2010),
(202,N'redemption.export',N'Export',N'redemption',N'Redemption',N'loyalty',N'Loyalty Management',N'export',2020),
(203,N'beat.view',N'View Listing',N'beat',N'Beats',N'beats',N'Beats Management',N'view',2030),
(204,N'beat.detail',N'View Detail',N'beat',N'Beats',N'beats',N'Beats Management',N'detail',2040),
(205,N'beat.create',N'Create',N'beat',N'Beats',N'beats',N'Beats Management',N'create',2050),
(206,N'beat.edit',N'Edit',N'beat',N'Beats',N'beats',N'Beats Management',N'edit',2060),
(207,N'beat.delete',N'Delete',N'beat',N'Beats',N'beats',N'Beats Management',N'delete',2070),
(208,N'beat_detail.view',N'View Listing',N'beat_detail',N'Beat Detail',N'beats',N'Beats Management',N'view',2080),
(209,N'checkin.view',N'View Listing',N'checkin',N'Checkin-Checkout',N'beats',N'Beats Management',N'view',2090),
(210,N'checkin.export',N'Export',N'checkin',N'Checkin-Checkout',N'beats',N'Beats Management',N'export',2100),
(211,N'visit_report.view',N'View Report',N'visit_report',N'Check In & Check Out Report',N'beats',N'Beats Management',N'view',2110),
(212,N'asr_performance_report.export',N'Export',N'asr_performance_report',N'ASR Performance',N'reports',N'Reports Management',N'export',2120),
(213,N'rating_report.view',N'View Report',N'rating_report',N'Rating Report',N'reports',N'Reports Management',N'view',2130),
(214,N'rating_report.export',N'Export',N'rating_report',N'Rating Report',N'reports',N'Reports Management',N'export',2140),
(215,N'activity_report.view',N'View Report',N'activity_report',N'Activity Reports',N'reports',N'Reports Management',N'view',2160),
(216,N'activity_report.export_sales_engineer',N'Sales Engineer Wise Export',N'activity_report',N'Activity Reports',N'reports',N'Reports Management',N'export_sales_engineer',2170),
(217,N'activity_report.export_distributor',N'Distributor Wise Export',N'activity_report',N'Activity Reports',N'reports',N'Reports Management',N'export_distributor',2180),
(218,N'activity_report.export_gift_summary',N'Gift Summary Export',N'activity_report',N'Activity Reports',N'reports',N'Reports Management',N'export_gift_summary',2190),
(219,N'retailer_performance_report.export',N'Export',N'retailer_performance_report',N'Retailer Performance',N'reports',N'Reports Management',N'export',2200),
(220,N'dealer_performance_report.export',N'Export',N'dealer_performance_report',N'Dealer Performance',N'reports',N'Reports Management',N'export',2210),
(221,N'market_intelligence_report.view',N'View Report',N'market_intelligence_report',N'Market Intelligence',N'reports',N'Reports Management',N'view',2220),
(222,N'market_intelligence_report.export',N'Export',N'market_intelligence_report',N'Market Intelligence',N'reports',N'Reports Management',N'export',2230),
(223,N'app_setting.view',N'View Setting',N'app_setting',N'FieldKonnect App Setting',N'settings',N'Setting Management',N'view',2240),
(224,N'app_setting.edit',N'Save Setting',N'app_setting',N'FieldKonnect App Setting',N'settings',N'Setting Management',N'edit',2250),
(225,N'dealer_portal_setting.view',N'View Setting',N'dealer_portal_setting',N'Dealer Portal Setting',N'settings',N'Setting Management',N'view',2260),
(226,N'role.view',N'View Listing',N'role',N'Roles',N'settings',N'Setting Management',N'view',2270),
(227,N'role.create',N'Create',N'role',N'Roles',N'settings',N'Setting Management',N'create',2280),
(228,N'role.edit',N'Edit',N'role',N'Roles',N'settings',N'Setting Management',N'edit',2290),
(229,N'role.delete',N'Delete',N'role',N'Roles',N'settings',N'Setting Management',N'delete',2300);
GO

-- Legacy names each permission replaces (185).
INSERT INTO #legacy (name,legacy_name,seq) VALUES
(N'dashboard.view',N'dashboard_access',0),
(N'dashboard.secondary_sales',N'dashboard_secondary',0),
(N'dashboard.loyalty',N'dashboard_loyalty',0),
(N'dashboard.activity',N'dashboard_activity',0),
(N'customer.view',N'customer_access',0),
(N'customer.detail',N'customer_show',0),
(N'customer.create',N'customer_create',0),
(N'customer.edit',N'customer_edit',0),
(N'customer.delete',N'customer_delete',0),
(N'customer.active',N'customer_active',0),
(N'customer.export',N'customer_download',0),
(N'customer.export',N'customers_report',1),
(N'customer.import',N'customer_upload',0),
(N'customer.template',N'customer_template',0),
(N'customer.approve',N'retailer_approve',0),
(N'customer.reject',N'retailer_reject',0),
(N'customer.pending',N'retailer_pending',0),
(N'customer.kyc_review',N'customer_kyc_access',0),
(N'country.view',N'country_access',0),
(N'country.create',N'country_create',0),
(N'country.edit',N'country_edit',0),
(N'country.delete',N'country_delete',0),
(N'country.active',N'country_active',0),
(N'country.export',N'country_download',0),
(N'country.import',N'country_upload',0),
(N'country.template',N'country_template',0),
(N'state.view',N'state_access',0),
(N'state.create',N'state_create',0),
(N'state.edit',N'state_edit',0),
(N'state.delete',N'state_delete',0),
(N'state.active',N'state_active',0),
(N'state.export',N'state_download',0),
(N'state.import',N'state_upload',0),
(N'state.template',N'state_template',0),
(N'district.view',N'district_access',0),
(N'district.create',N'district_create',0),
(N'district.edit',N'district_edit',0),
(N'district.delete',N'district_delete',0),
(N'district.active',N'district_active',0),
(N'district.export',N'district_download',0),
(N'district.import',N'district_upload',0),
(N'district.template',N'district_template',0),
(N'city.view',N'city_access',0),
(N'city.create',N'city_create',0),
(N'city.edit',N'city_edit',0),
(N'city.delete',N'city_delete',0),
(N'city.active',N'city_active',0),
(N'city.export',N'city_download',0),
(N'city.import',N'city_upload',0),
(N'city.template',N'city_template',0),
(N'pincode.view',N'pincode_access',0),
(N'pincode.create',N'pincode_create',0),
(N'pincode.edit',N'pincode_edit',0),
(N'pincode.delete',N'pincode_delete',0),
(N'pincode.active',N'pincode_active',0),
(N'pincode.export',N'pincode_download',0),
(N'pincode.import',N'pincode_upload',0),
(N'pincode.template',N'pincode_template',0),
(N'city_assignment.view',N'city_assigned',0),
(N'segment.view',N'category_access',0);
INSERT INTO #legacy (name,legacy_name,seq) VALUES
(N'segment.create',N'category_create',0),
(N'segment.edit',N'category_edit',0),
(N'segment.delete',N'category_delete',0),
(N'segment.active',N'category_active',0),
(N'segment.export',N'category_download',0),
(N'segment.import',N'category_upload',0),
(N'segment.template',N'category_template',0),
(N'family.view',N'subcategory_access',0),
(N'family.create',N'subcategory_create',0),
(N'family.edit',N'subcategory_edit',0),
(N'family.delete',N'subcategory_delete',0),
(N'family.active',N'subcategory_active',0),
(N'family.export',N'subcategory_download',0),
(N'family.import',N'subcategory_upload',0),
(N'family.template',N'subcategory_template',0),
(N'product.view',N'product_access',0),
(N'product.create',N'product_create',0),
(N'product.edit',N'product_edit',0),
(N'product.delete',N'product_delete',0),
(N'product.active',N'product_active',0),
(N'product.export',N'product_download',0),
(N'product.import',N'product_upload',0),
(N'product.template',N'product_template',0),
(N'attendance.view',N'attendance_report',0),
(N'attendance.delete',N'attendance_delete',0),
(N'attendance_summary.view',N'attendance_summary_report',0),
(N'holiday.view',N'holiday_access',0),
(N'leave.view',N'leave_access',0),
(N'tour.view',N'tours',0),
(N'branch.view',N'branch',0),
(N'branch.export',N'branch_report_download',0),
(N'zone.view',N'division',0),
(N'zone.export',N'division_report_download',0),
(N'designation.view',N'designation',0),
(N'department.view',N'departments',0),
(N'department.export',N'department_report_download',0),
(N'user.view',N'user_access',0),
(N'user.create',N'user_create',0),
(N'user.edit',N'user_edit',0),
(N'user.delete',N'user_delete',0),
(N'user.active',N'user_active',0),
(N'user.export',N'user_download',0),
(N'user.import',N'user_upload',0),
(N'user.template',N'user_template',0),
(N'user_app.view',N'user_app_details_access',0),
(N'user_app.force_logout',N'user_app_force_logout',0),
(N'user_app.reset_device',N'user_app_uuid_reset',0),
(N'user_activity.view',N'user_location',0),
(N'user_target.view',N'target_access',0),
(N'user_target.view',N'target_users_access',1),
(N'user_target.create',N'target_users_access_create',0),
(N'user_target.edit',N'target_users_access_edit',0),
(N'user_target.delete',N'target_users_access_delete',0),
(N'user_target.export',N'sales_target_users_download',0),
(N'user_target.import',N'sales_target_users_upload',0),
(N'user_target.template',N'sales_target_users_template',0),
(N'expense_type.view',N'expenses_type',0),
(N'expense_type.create',N'expenses_type_create',0),
(N'expense_type.edit',N'expenses_type_update',0),
(N'expense.view',N'expense_access',0);
INSERT INTO #legacy (name,legacy_name,seq) VALUES
(N'expense.create',N'expenses_create',0),
(N'expense.edit',N'expenses_edit',0),
(N'expense.delete',N'expenses_delete',0),
(N'expense.check',N'expense_checked',0),
(N'expense.approve',N'expenses_authority',0),
(N'order.view',N'order_access',0),
(N'order.detail',N'order_show',0),
(N'order.create',N'order_create',0),
(N'order.edit',N'order_edit',0),
(N'order.delete',N'order_delete',0),
(N'order.active',N'order_active',0),
(N'order.export',N'order_download',0),
(N'order.dispatch',N'order_dispatch',0),
(N'order_dispatch.view',N'sale_access',0),
(N'order_dispatch.detail',N'sale_show',0),
(N'invoice_transaction.view',N'new_invoice_access',0),
(N'invoice_transaction.create',N'new_invoice_create',0),
(N'invoice_transaction.edit',N'new_invoice_edit',0),
(N'invoice_transaction.delete',N'new_invoice_delete',0),
(N'invoice_transaction.export',N'new_invoice_export',0),
(N'invoice_transaction.approve_ss',N'new_invoice_approve_ss',0),
(N'invoice_transaction.approve_sales',N'new_invoice_approve_sales',0),
(N'invoice_transaction.approve_ho',N'new_invoice_approve_ho',0),
(N'invoice_transaction.hold',N'new_invoice_hold',0),
(N'invoice_transaction.reject',N'new_invoice_reject',0),
(N'scheme.view',N'scheme_access_list',0),
(N'scheme.view',N'scheme_access',1),
(N'scheme.detail',N'scheme_show',0),
(N'scheme.create',N'scheme_create',0),
(N'scheme.edit',N'scheme_edit',0),
(N'scheme.delete',N'scheme_delete',0),
(N'scheme.draft',N'scheme_draft',0),
(N'scheme.submit',N'scheme_submit',0),
(N'scheme.approve',N'scheme_approve',0),
(N'scheme.reject',N'scheme_reject',0),
(N'scheme.publish',N'scheme_publish',0),
(N'redemption.view',N'redemption_access',0),
(N'redemption.export',N'redemption_download',0),
(N'beat.view',N'beat_access',0),
(N'beat.detail',N'beat_show',0),
(N'beat.create',N'beat_create',0),
(N'beat.edit',N'beat_edit',0),
(N'beat.delete',N'beat_delete',0),
(N'beat_detail.view',N'beatdetail_access',0),
(N'checkin.view',N'checkin_access',0),
(N'checkin.export',N'checkin_download',0),
(N'visit_report.view',N'visit_report',0),
(N'asr_performance_report.export',N'ASR_report_Download',0),
(N'rating_report.view',N'asm_rating_report',0),
(N'rating_report.export',N'asm_rating_download',0),
(N'rating_report.export',N'asm_rating_detailed_download',1),
(N'activity_report.view',N'activity_report_access',0),
(N'activity_report.export_sales_engineer',N'activity_report_sales_engineer_download',0),
(N'activity_report.export_distributor',N'activity_report_distributor_download',0),
(N'activity_report.export_gift_summary',N'activity_report_gift_summary_download',0),
(N'retailer_performance_report.export',N'retailer_productivity_report',0),
(N'market_intelligence_report.view',N'market_intelligence_access',0),
(N'market_intelligence_report.export',N'market_intelligence_report_download',0),
(N'app_setting.view',N'loyalty_app_setting_access',0),
(N'app_setting.view',N'field_konnect_app_setting_access',1);
INSERT INTO #legacy (name,legacy_name,seq) VALUES
(N'dealer_portal_setting.view',N'dealer_portal_setting_access',0),
(N'role.view',N'role_access',0),
(N'role.create',N'role_create',0),
(N'role.edit',N'role_edit',0),
(N'role.delete',N'role_delete',0);
GO

-- Permissions with no row of their own before V7.3: who should receive them (52).
INSERT INTO #inherit (name,source_name) VALUES
(N'city_assignment.create',N'city_assigned'),
(N'city_assignment.delete',N'city_assigned'),
(N'city_assignment.export',N'city_assigned'),
(N'city_assignment.import',N'city_assigned'),
(N'city_assignment.template',N'city_assigned'),
(N'attendance.punch_in',N'attendance_report'),
(N'attendance.punch_out',N'attendance_report'),
(N'attendance.approve',N'attendance_report'),
(N'attendance.reject',N'attendance_report'),
(N'attendance.export',N'attendance_report'),
(N'attendance_summary.export',N'attendance_summary_report'),
(N'holiday.create',N'holiday_access'),
(N'holiday.edit',N'holiday_access'),
(N'holiday.delete',N'holiday_access'),
(N'holiday.export',N'holiday_access'),
(N'leave.create',N'leave_access'),
(N'leave.delete',N'leave_access'),
(N'leave.approve',N'leave_access'),
(N'leave.reject',N'leave_access'),
(N'leave.comp_off',N'leave_access'),
(N'leave.export',N'leave_access'),
(N'tour.create',N'tours'),
(N'tour.edit',N'tours'),
(N'tour.delete',N'tours'),
(N'tour.status',N'tours'),
(N'tour.export',N'tours'),
(N'tour.import',N'tours'),
(N'tour.template',N'tours'),
(N'branch.create',N'branch'),
(N'branch.edit',N'branch'),
(N'branch.delete',N'branch'),
(N'branch.active',N'branch'),
(N'zone.create',N'division'),
(N'zone.edit',N'division'),
(N'zone.delete',N'division'),
(N'zone.active',N'division'),
(N'designation.create',N'designation'),
(N'designation.edit',N'designation'),
(N'designation.delete',N'designation'),
(N'designation.active',N'designation'),
(N'designation.export',N'designation'),
(N'department.create',N'departments'),
(N'department.edit',N'departments'),
(N'department.delete',N'departments'),
(N'department.active',N'departments'),
(N'expense_type.delete',N'expenses_type_update'),
(N'expense_type.active',N'expenses_type_update'),
(N'invoice_transaction.detail',N'new_invoice_access'),
(N'invoice_transaction.detail',N'invoice_transaction.view'),
(N'dealer_performance_report.export',N'retailer_productivity_report'),
(N'app_setting.edit',N'loyalty_app_setting_access'),
(N'app_setting.edit',N'field_konnect_app_setting_access');
GO


-- ---------------------------------------------------------------------------
-- STEP 4. The rebuild, in one transaction.
-- ---------------------------------------------------------------------------
BEGIN TRANSACTION;

-- 4a. What every role can do right now, by permission name. Captured before
--     anything changes; the back-fill below is decided entirely from this.
IF OBJECT_ID('tempdb..#granted') IS NOT NULL DROP TABLE #granted;
SELECT rp.role_id, p.name AS permission_name
INTO #granted
FROM role_has_permissions rp
INNER JOIN permissions p ON p.id = rp.permission_id;
CREATE INDEX ix_granted_name ON #granted (permission_name);

DECLARE @permissions_before INT = (SELECT COUNT(*) FROM permissions);
DECLARE @assignments_before INT = (SELECT COUNT(*) FROM role_has_permissions);

-- 4b. One row per permission name. Where the legacy data holds the same name
--     twice, the lowest id wins - the same row the API would keep.
IF OBJECT_ID('tempdb..#by_name') IS NOT NULL DROP TABLE #by_name;
SELECT name, MIN(id) AS id INTO #by_name FROM permissions GROUP BY name;

-- 4c. Every existing row a catalog entry could adopt: its own name first,
--     then each legacy name in the order the catalog lists them.
IF OBJECT_ID('tempdb..#cand') IS NOT NULL DROP TABLE #cand;
SELECT c.ordinal, r.id AS permission_id, 0 AS priority
INTO #cand
FROM #catalog c INNER JOIN #by_name r ON r.name = c.name
UNION ALL
SELECT c.ordinal, r.id, l.seq + 1
FROM #catalog c
INNER JOIN #legacy l ON l.name = c.name
INNER JOIN #by_name r ON r.name = l.legacy_name;

-- 4d. The row each catalog entry keeps - it keeps its id, so every role keeps
--     what it was granted.
IF OBJECT_ID('tempdb..#keeper') IS NOT NULL DROP TABLE #keeper;
SELECT ordinal, permission_id
INTO #keeper
FROM (SELECT ordinal, permission_id,
             ROW_NUMBER() OVER (PARTITION BY ordinal ORDER BY priority, permission_id) AS rn
      FROM #cand) t
WHERE rn = 1;

-- 4e. The extra copies of a merged permission. Their role assignments move to
--     the row that survives, so a role holding only the extra copy keeps it.
IF OBJECT_ID('tempdb..#merged') IS NOT NULL DROP TABLE #merged;
SELECT c.permission_id, k.permission_id AS keeper_id
INTO #merged
FROM #cand c
INNER JOIN #keeper k ON k.ordinal = c.ordinal
WHERE c.permission_id <> k.permission_id;

INSERT INTO role_has_permissions (permission_id, role_id)
SELECT DISTINCT m.keeper_id, s.role_id
FROM #merged m
INNER JOIN role_has_permissions s ON s.permission_id = m.permission_id
WHERE NOT EXISTS (SELECT 1 FROM role_has_permissions t
                  WHERE t.permission_id = m.keeper_id AND t.role_id = s.role_id);

-- 4f. Permissions are granted through roles only. Any per-user grant the legacy
--     system left behind goes, so what a user may do is what their roles allow.
DELETE FROM model_has_permissions;

-- 4g. Everything the CRM does not enforce, plus the merged-away duplicates.
IF OBJECT_ID('tempdb..#removable') IS NOT NULL DROP TABLE #removable;
SELECT p.id INTO #removable FROM permissions p
WHERE NOT EXISTS (SELECT 1 FROM #keeper k WHERE k.permission_id = p.id);

DECLARE @removed INT = (SELECT COUNT(*) FROM #removable);

DELETE rp FROM role_has_permissions rp INNER JOIN #removable r ON r.id = rp.permission_id;
DELETE mp FROM model_has_permissions mp INNER JOIN #removable r ON r.id = mp.permission_id;
DELETE p  FROM permissions p          INNER JOIN #removable r ON r.id = p.id;

-- 4h. Rename the survivors and fill in the catalog metadata. This happens only
--     after the duplicates are gone, so the unique index on (name, guard_name)
--     never sees two rows claiming the same name.
UPDATE p
SET p.name         = c.name,
    p.guard_name   = 'users',
    p.label        = c.label,
    p.group_key    = c.group_key,
    p.group_label  = c.group_label,
    p.module_key   = c.module_key,
    p.module_label = c.module_label,
    p.action_key   = c.action_key,
    p.sort_order   = c.sort_order,
    p.updated_at   = SYSUTCDATETIME()
FROM permissions p
INNER JOIN #keeper k ON k.permission_id = p.id
INNER JOIN #catalog c ON c.ordinal = k.ordinal;

-- 4i. Catalog entries with no row at all: the actions V7.3 splits out for the
--     first time. Created after the rename so no name collides.
IF OBJECT_ID('tempdb..#created') IS NOT NULL DROP TABLE #created;
SELECT c.ordinal, c.name INTO #created
FROM #catalog c
WHERE NOT EXISTS (SELECT 1 FROM #keeper k WHERE k.ordinal = c.ordinal);

DECLARE @created INT = (SELECT COUNT(*) FROM #created);

INSERT INTO permissions (name, guard_name, label, group_key, group_label, module_key, module_label, action_key, sort_order, created_at, updated_at)
SELECT c.name, 'users', c.label, c.group_key, c.group_label, c.module_key, c.module_label, c.action_key, c.sort_order, SYSUTCDATETIME(), SYSUTCDATETIME()
FROM #catalog c INNER JOIN #created n ON n.ordinal = c.ordinal;

-- 4j. Every catalog entry mapped to its row, kept and created alike.
IF OBJECT_ID('tempdb..#final') IS NOT NULL DROP TABLE #final;
SELECT c.ordinal, p.id AS permission_id
INTO #final
FROM #catalog c INNER JOIN permissions p ON p.name = c.name;

-- 4k. An action that used to share one permission with the rest of its module -
--     every holiday action sat behind holiday_access - now has its own. Roles
--     that held the shared permission receive the new ones, so the split takes
--     nothing away. Only entries this run created, or whose legacy name was
--     still on a role, are back-filled: both happen once.
IF OBJECT_ID('tempdb..#sources') IS NOT NULL DROP TABLE #sources;
SELECT name, legacy_name AS source_name INTO #sources FROM #legacy
UNION
SELECT name, source_name FROM #inherit;

INSERT INTO role_has_permissions (role_id, permission_id)
SELECT DISTINCT g.role_id, f.permission_id
FROM #catalog c
INNER JOIN #final   f ON f.ordinal = c.ordinal
INNER JOIN #sources s ON s.name = c.name
INNER JOIN #granted g ON g.permission_name = s.source_name
WHERE (EXISTS (SELECT 1 FROM #created n WHERE n.ordinal = c.ordinal)
       OR EXISTS (SELECT 1 FROM #legacy l INNER JOIN #granted g2 ON g2.permission_name = l.legacy_name
                  WHERE l.name = c.name))
  AND NOT EXISTS (SELECT 1 FROM role_has_permissions rp
                  WHERE rp.role_id = g.role_id AND rp.permission_id = f.permission_id);

-- 4l. superadmin holds everything, which is what the CRM and the API assume.
INSERT INTO role_has_permissions (role_id, permission_id)
SELECT r.id, f.permission_id
FROM roles r CROSS JOIN #final f
WHERE r.name = 'superadmin'
  AND NOT EXISTS (SELECT 1 FROM role_has_permissions rp
                  WHERE rp.role_id = r.id AND rp.permission_id = f.permission_id);

-- 4m. Roles were split across the 'web' and 'users' guards while the permissions
--     were all on 'users'. Nothing reads the guard, so they come onto one.
UPDATE roles SET guard_name = 'users' WHERE guard_name <> 'users';

SELECT
    N'Permissions before'  AS [step], @permissions_before AS [value] UNION ALL
SELECT N'Permissions after',          (SELECT COUNT(*) FROM permissions) UNION ALL
SELECT N'  of which created now',     @created UNION ALL
SELECT N'  legacy rows removed',      @removed UNION ALL
SELECT N'Role assignments before',    @assignments_before UNION ALL
SELECT N'Role assignments after',     (SELECT COUNT(*) FROM role_has_permissions);

COMMIT TRANSACTION;
GO

-- ---------------------------------------------------------------------------
-- STEP 5. Verification. Every line must read OK.
-- ---------------------------------------------------------------------------
SELECT N'1. Catalog columns present' AS [check],
       CASE WHEN COUNT(*) = 7 THEN N'OK' ELSE N'FAILED' END AS [result], COUNT(*) AS [value]
FROM sys.columns WHERE object_id = OBJECT_ID('permissions')
  AND name IN ('label','group_key','group_label','module_key','module_label','action_key','sort_order');

SELECT N'2. Migration recorded' AS [check],
       CASE WHEN COUNT(*) = 1 THEN N'OK' ELSE N'FAILED' END AS [result], COUNT(*) AS [value]
FROM __EFMigrationsHistory WHERE MigrationId = N'20260825103753_AddPermissionCatalogMetadata';

SELECT N'3. Permission count matches the catalog' AS [check],
       CASE WHEN COUNT(*) = 230 THEN N'OK' ELSE N'FAILED' END AS [result], COUNT(*) AS [value]
FROM permissions;

SELECT N'4. Every permission carries its metadata' AS [check],
       CASE WHEN COUNT(*) = 0 THEN N'OK' ELSE N'FAILED' END AS [result], COUNT(*) AS [value]
FROM permissions WHERE module_key IS NULL OR action_key IS NULL;

SELECT N'5. No legacy permission names left' AS [check],
       CASE WHEN COUNT(*) = 0 THEN N'OK' ELSE N'FAILED' END AS [result], COUNT(*) AS [value]
FROM permissions WHERE name NOT LIKE '%.%';

SELECT N'6. No direct per-user grants' AS [check],
       CASE WHEN COUNT(*) = 0 THEN N'OK' ELSE N'FAILED' END AS [result], COUNT(*) AS [value]
FROM model_has_permissions;

SELECT N'7. All roles on the users guard' AS [check],
       CASE WHEN COUNT(*) = 0 THEN N'OK' ELSE N'FAILED' END AS [result], COUNT(*) AS [value]
FROM roles WHERE guard_name <> 'users';

SELECT N'8. No orphan role assignments' AS [check],
       CASE WHEN COUNT(*) = 0 THEN N'OK' ELSE N'FAILED' END AS [result], COUNT(*) AS [value]
FROM role_has_permissions rp
WHERE NOT EXISTS (SELECT 1 FROM permissions p WHERE p.id = rp.permission_id);

SELECT N'9. superadmin holds every permission' AS [check],
       CASE WHEN (SELECT COUNT(*) FROM role_has_permissions rp INNER JOIN roles r ON r.id = rp.role_id AND r.name = 'superadmin')
                 = (SELECT COUNT(*) FROM permissions) THEN N'OK' ELSE N'CHECK - superadmin is short' END AS [result],
       (SELECT COUNT(*) FROM role_has_permissions rp INNER JOIN roles r ON r.id = rp.role_id AND r.name = 'superadmin') AS [value];
GO

-- What every role can do now, for the record.
SELECT r.id AS [role_id], r.name AS [role_name],
       COUNT(rp.permission_id) AS [permissions],
       (SELECT COUNT(*) FROM model_has_roles mr INNER JOIN users u ON u.id = mr.model_id AND u.deleted_at IS NULL
         WHERE mr.role_id = r.id) AS [users]
FROM roles r LEFT JOIN role_has_permissions rp ON rp.role_id = r.id
GROUP BY r.id, r.name ORDER BY r.id;
GO

PRINT '== done. Recycle the API app pool now. ==';
GO
