namespace Domain.Constants;

/// <summary>One permission the CRM actually enforces: its canonical name, where it
/// belongs in the role matrix, and the legacy names it replaces.</summary>
/// <param name="LegacyNames">Old permission rows renamed onto this one. Role assignments follow the rename.</param>
/// <param name="InheritFrom">For an action that had no permission of its own before, the
/// legacy names whose holders should receive it, so no role loses what it could already do.</param>
public sealed record PermissionDefinition(
    string Name,
    string Label,
    string ModuleKey,
    string ModuleLabel,
    string GroupKey,
    string GroupLabel,
    string ActionKey,
    int SortOrder,
    IReadOnlyList<string> LegacyNames,
    IReadOnlyList<string> InheritFrom);

/// <summary>The single source of truth for permissions. The permissions table is synced
/// to this list on startup: entries are created or renamed onto it, metadata is refreshed,
/// and any permission row outside this list is removed.</summary>
public static class PermissionCatalog
{
    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        // ---------- Dashboard ----------
        // Dashboard
        new("dashboard.view", "View Dashboard", "dashboard", "Dashboard", "dashboard", "Dashboard", "view", 10, ["dashboard_access"], []),
        new("dashboard.secondary_sales", "Secondary Sales Tab", "dashboard", "Dashboard", "dashboard", "Dashboard", "secondary_sales", 20, ["dashboard_secondary"], []),
        new("dashboard.loyalty", "Loyalty Tab", "dashboard", "Dashboard", "dashboard", "Dashboard", "loyalty", 30, ["dashboard_loyalty"], []),
        new("dashboard.activity", "Activity Tab", "dashboard", "Dashboard", "dashboard", "Dashboard", "activity", 40, ["dashboard_activity"], []),
        // ---------- Customers Management ----------
        // Customers
        new("customer.view", "View Listing", "customer", "Master", "customers", "Customers Management", "view", 50, ["customer_access"], []),
        new("customer.detail", "View Detail", "customer", "Master", "customers", "Customers Management", "detail", 60, ["customer_show"], []),
        new("customer.create", "Create", "customer", "Master", "customers", "Customers Management", "create", 70, ["customer_create"], []),
        new("customer.edit", "Edit", "customer", "Master", "customers", "Customers Management", "edit", 80, ["customer_edit"], []),
        new("customer.delete", "Delete", "customer", "Master", "customers", "Customers Management", "delete", 90, ["customer_delete"], []),
        new("customer.active", "Activate / Deactivate", "customer", "Master", "customers", "Customers Management", "active", 100, ["customer_active"], []),
        new("customer.export", "Export", "customer", "Master", "customers", "Customers Management", "export", 110, ["customer_download", "customers_report"], []),
        new("customer.import", "Import", "customer", "Master", "customers", "Customers Management", "import", 120, ["customer_upload"], []),
        new("customer.template", "Download Template", "customer", "Master", "customers", "Customers Management", "template", 130, ["customer_template"], []),
        new("customer.approve", "Approve Retailer", "customer", "Master", "customers", "Customers Management", "approve", 140, ["retailer_approve"], []),
        new("customer.reject", "Reject Retailer", "customer", "Master", "customers", "Customers Management", "reject", 150, ["retailer_reject"], []),
        new("customer.pending", "Mark Retailer Pending", "customer", "Master", "customers", "Customers Management", "pending", 160, ["retailer_pending"], []),
        new("customer.kyc_review", "Approve / Reject KYC", "customer", "Master", "customers", "Customers Management", "kyc_review", 170, ["customer_kyc_access"], []),
        // KYC - its own menu under Customers Management. Whoever already reviews KYC on the
        // customer screen gets the new screen too, so the split does not take anything away.
        new("customer_kyc.view", "View Listing", "customer_kyc", "KYC", "customers", "Customers Management", "view", 175, [], ["customer.kyc_review"]),
        // ---------- Address Management ----------
        // Country
        new("country.view", "View Listing", "country", "Country", "address", "Address Management", "view", 180, ["country_access"], []),
        new("country.create", "Create", "country", "Country", "address", "Address Management", "create", 190, ["country_create"], []),
        new("country.edit", "Edit", "country", "Country", "address", "Address Management", "edit", 200, ["country_edit"], []),
        new("country.delete", "Delete", "country", "Country", "address", "Address Management", "delete", 210, ["country_delete"], []),
        new("country.active", "Activate / Deactivate", "country", "Country", "address", "Address Management", "active", 220, ["country_active"], []),
        new("country.export", "Export", "country", "Country", "address", "Address Management", "export", 230, ["country_download"], []),
        new("country.import", "Import", "country", "Country", "address", "Address Management", "import", 240, ["country_upload"], []),
        new("country.template", "Download Template", "country", "Country", "address", "Address Management", "template", 250, ["country_template"], []),
        // State
        new("state.view", "View Listing", "state", "State", "address", "Address Management", "view", 260, ["state_access"], []),
        new("state.create", "Create", "state", "State", "address", "Address Management", "create", 270, ["state_create"], []),
        new("state.edit", "Edit", "state", "State", "address", "Address Management", "edit", 280, ["state_edit"], []),
        new("state.delete", "Delete", "state", "State", "address", "Address Management", "delete", 290, ["state_delete"], []),
        new("state.active", "Activate / Deactivate", "state", "State", "address", "Address Management", "active", 300, ["state_active"], []),
        new("state.export", "Export", "state", "State", "address", "Address Management", "export", 310, ["state_download"], []),
        new("state.import", "Import", "state", "State", "address", "Address Management", "import", 320, ["state_upload"], []),
        new("state.template", "Download Template", "state", "State", "address", "Address Management", "template", 330, ["state_template"], []),
        // District
        new("district.view", "View Listing", "district", "District", "address", "Address Management", "view", 340, ["district_access"], []),
        new("district.create", "Create", "district", "District", "address", "Address Management", "create", 350, ["district_create"], []),
        new("district.edit", "Edit", "district", "District", "address", "Address Management", "edit", 360, ["district_edit"], []),
        new("district.delete", "Delete", "district", "District", "address", "Address Management", "delete", 370, ["district_delete"], []),
        new("district.active", "Activate / Deactivate", "district", "District", "address", "Address Management", "active", 380, ["district_active"], []),
        new("district.export", "Export", "district", "District", "address", "Address Management", "export", 390, ["district_download"], []),
        new("district.import", "Import", "district", "District", "address", "Address Management", "import", 400, ["district_upload"], []),
        new("district.template", "Download Template", "district", "District", "address", "Address Management", "template", 410, ["district_template"], []),
        // City
        new("city.view", "View Listing", "city", "City", "address", "Address Management", "view", 420, ["city_access"], []),
        new("city.create", "Create", "city", "City", "address", "Address Management", "create", 430, ["city_create"], []),
        new("city.edit", "Edit", "city", "City", "address", "Address Management", "edit", 440, ["city_edit"], []),
        new("city.delete", "Delete", "city", "City", "address", "Address Management", "delete", 450, ["city_delete"], []),
        new("city.active", "Activate / Deactivate", "city", "City", "address", "Address Management", "active", 460, ["city_active"], []),
        new("city.export", "Export", "city", "City", "address", "Address Management", "export", 470, ["city_download"], []),
        new("city.import", "Import", "city", "City", "address", "Address Management", "import", 480, ["city_upload"], []),
        new("city.template", "Download Template", "city", "City", "address", "Address Management", "template", 490, ["city_template"], []),
        // Pincode
        new("pincode.view", "View Listing", "pincode", "Pincode", "address", "Address Management", "view", 500, ["pincode_access"], []),
        new("pincode.create", "Create", "pincode", "Pincode", "address", "Address Management", "create", 510, ["pincode_create"], []),
        new("pincode.edit", "Edit", "pincode", "Pincode", "address", "Address Management", "edit", 520, ["pincode_edit"], []),
        new("pincode.delete", "Delete", "pincode", "Pincode", "address", "Address Management", "delete", 530, ["pincode_delete"], []),
        new("pincode.active", "Activate / Deactivate", "pincode", "Pincode", "address", "Address Management", "active", 540, ["pincode_active"], []),
        new("pincode.export", "Export", "pincode", "Pincode", "address", "Address Management", "export", 550, ["pincode_download"], []),
        new("pincode.import", "Import", "pincode", "Pincode", "address", "Address Management", "import", 560, ["pincode_upload"], []),
        new("pincode.template", "Download Template", "pincode", "Pincode", "address", "Address Management", "template", 570, ["pincode_template"], []),
        // City Assigned
        new("city_assignment.view", "View Listing", "city_assignment", "City Assigned", "address", "Address Management", "view", 580, ["city_assigned"], []),
        new("city_assignment.create", "Assign City", "city_assignment", "City Assigned", "address", "Address Management", "create", 590, [], ["city_assigned"]),
        new("city_assignment.delete", "Remove Assignment", "city_assignment", "City Assigned", "address", "Address Management", "delete", 600, [], ["city_assigned"]),
        new("city_assignment.export", "Export", "city_assignment", "City Assigned", "address", "Address Management", "export", 610, [], ["city_assigned"]),
        new("city_assignment.import", "Import", "city_assignment", "City Assigned", "address", "Address Management", "import", 620, [], ["city_assigned"]),
        new("city_assignment.template", "Download Template", "city_assignment", "City Assigned", "address", "Address Management", "template", 630, [], ["city_assigned"]),
        // ---------- Product Management ----------
        // Segment
        new("segment.view", "View Listing", "segment", "Segment", "products", "Product Management", "view", 640, ["category_access"], []),
        new("segment.create", "Create", "segment", "Segment", "products", "Product Management", "create", 650, ["category_create"], []),
        new("segment.edit", "Edit", "segment", "Segment", "products", "Product Management", "edit", 660, ["category_edit"], []),
        new("segment.delete", "Delete", "segment", "Segment", "products", "Product Management", "delete", 670, ["category_delete"], []),
        new("segment.active", "Activate / Deactivate", "segment", "Segment", "products", "Product Management", "active", 680, ["category_active"], []),
        new("segment.export", "Export", "segment", "Segment", "products", "Product Management", "export", 690, ["category_download"], []),
        new("segment.import", "Import", "segment", "Segment", "products", "Product Management", "import", 700, ["category_upload"], []),
        new("segment.template", "Download Template", "segment", "Segment", "products", "Product Management", "template", 710, ["category_template"], []),
        // Family
        new("family.view", "View Listing", "family", "Family", "products", "Product Management", "view", 720, ["subcategory_access"], []),
        new("family.create", "Create", "family", "Family", "products", "Product Management", "create", 730, ["subcategory_create"], []),
        new("family.edit", "Edit", "family", "Family", "products", "Product Management", "edit", 740, ["subcategory_edit"], []),
        new("family.delete", "Delete", "family", "Family", "products", "Product Management", "delete", 750, ["subcategory_delete"], []),
        new("family.active", "Activate / Deactivate", "family", "Family", "products", "Product Management", "active", 760, ["subcategory_active"], []),
        new("family.export", "Export", "family", "Family", "products", "Product Management", "export", 770, ["subcategory_download"], []),
        new("family.import", "Import", "family", "Family", "products", "Product Management", "import", 780, ["subcategory_upload"], []),
        new("family.template", "Download Template", "family", "Family", "products", "Product Management", "template", 790, ["subcategory_template"], []),
        // Products
        new("product.view", "View Listing", "product", "Products", "products", "Product Management", "view", 800, ["product_access"], []),
        new("product.create", "Create", "product", "Products", "products", "Product Management", "create", 810, ["product_create"], []),
        new("product.edit", "Edit", "product", "Products", "products", "Product Management", "edit", 820, ["product_edit"], []),
        new("product.delete", "Delete", "product", "Products", "products", "Product Management", "delete", 830, ["product_delete"], []),
        new("product.active", "Activate / Deactivate", "product", "Products", "products", "Product Management", "active", 840, ["product_active"], []),
        new("product.export", "Export", "product", "Products", "products", "Product Management", "export", 850, ["product_download"], []),
        new("product.import", "Import", "product", "Products", "products", "Product Management", "import", 860, ["product_upload"], []),
        new("product.template", "Download Template", "product", "Products", "products", "Product Management", "template", 870, ["product_template"], []),
        // ---------- HR Management ----------
        // Attendance Details
        new("attendance.view", "View Listing", "attendance", "Attendance Details", "hr", "HR Management", "view", 880, ["attendance_report"], []),
        new("attendance.punch_in", "Punch In", "attendance", "Attendance Details", "hr", "HR Management", "punch_in", 890, [], ["attendance_report"]),
        new("attendance.punch_out", "Punch Out", "attendance", "Attendance Details", "hr", "HR Management", "punch_out", 900, [], ["attendance_report"]),
        new("attendance.approve", "Approve", "attendance", "Attendance Details", "hr", "HR Management", "approve", 910, [], ["attendance_report"]),
        new("attendance.reject", "Reject", "attendance", "Attendance Details", "hr", "HR Management", "reject", 920, [], ["attendance_report"]),
        new("attendance.delete", "Delete", "attendance", "Attendance Details", "hr", "HR Management", "delete", 930, ["attendance_delete"], []),
        new("attendance.export", "Export", "attendance", "Attendance Details", "hr", "HR Management", "export", 940, [], ["attendance_report"]),
        // Attendance Summary
        new("attendance_summary.view", "View Listing", "attendance_summary", "Attendance Summary", "hr", "HR Management", "view", 950, ["attendance_summary_report"], []),
        new("attendance_summary.export", "Export", "attendance_summary", "Attendance Summary", "hr", "HR Management", "export", 960, [], ["attendance_summary_report"]),
        // Holidays
        new("holiday.view", "View Listing", "holiday", "Holidays", "hr", "HR Management", "view", 970, ["holiday_access"], []),
        new("holiday.create", "Create", "holiday", "Holidays", "hr", "HR Management", "create", 980, [], ["holiday_access"]),
        new("holiday.edit", "Edit", "holiday", "Holidays", "hr", "HR Management", "edit", 990, [], ["holiday_access"]),
        new("holiday.delete", "Delete", "holiday", "Holidays", "hr", "HR Management", "delete", 1000, [], ["holiday_access"]),
        new("holiday.export", "Export", "holiday", "Holidays", "hr", "HR Management", "export", 1010, [], ["holiday_access"]),
        // Leaves
        new("leave.view", "View Listing", "leave", "Leaves", "hr", "HR Management", "view", 1020, ["leave_access"], []),
        new("leave.create", "Create", "leave", "Leaves", "hr", "HR Management", "create", 1030, [], ["leave_access"]),
        new("leave.delete", "Delete", "leave", "Leaves", "hr", "HR Management", "delete", 1040, [], ["leave_access"]),
        new("leave.approve", "Approve", "leave", "Leaves", "hr", "HR Management", "approve", 1050, [], ["leave_access"]),
        new("leave.reject", "Reject", "leave", "Leaves", "hr", "HR Management", "reject", 1060, [], ["leave_access"]),
        new("leave.comp_off", "Add Comp Off", "leave", "Leaves", "hr", "HR Management", "comp_off", 1070, [], ["leave_access"]),
        new("leave.export", "Export", "leave", "Leaves", "hr", "HR Management", "export", 1080, [], ["leave_access"]),
        // Tours
        new("tour.view", "View Listing", "tour", "Tours", "hr", "HR Management", "view", 1090, ["tours"], []),
        new("tour.create", "Create", "tour", "Tours", "hr", "HR Management", "create", 1100, [], ["tours"]),
        new("tour.edit", "Edit", "tour", "Tours", "hr", "HR Management", "edit", 1110, [], ["tours"]),
        new("tour.delete", "Delete", "tour", "Tours", "hr", "HR Management", "delete", 1120, [], ["tours"]),
        new("tour.status", "Approve / Reject", "tour", "Tours", "hr", "HR Management", "status", 1130, [], ["tours"]),
        new("tour.export", "Export", "tour", "Tours", "hr", "HR Management", "export", 1140, [], ["tours"]),
        new("tour.import", "Import", "tour", "Tours", "hr", "HR Management", "import", 1150, [], ["tours"]),
        new("tour.template", "Download Template", "tour", "Tours", "hr", "HR Management", "template", 1160, [], ["tours"]),
        // Branch
        new("branch.view", "View Listing", "branch", "Branch", "hr", "HR Management", "view", 1170, ["branch"], []),
        new("branch.create", "Create", "branch", "Branch", "hr", "HR Management", "create", 1180, [], ["branch"]),
        new("branch.edit", "Edit", "branch", "Branch", "hr", "HR Management", "edit", 1190, [], ["branch"]),
        new("branch.delete", "Delete", "branch", "Branch", "hr", "HR Management", "delete", 1200, [], ["branch"]),
        new("branch.active", "Activate / Deactivate", "branch", "Branch", "hr", "HR Management", "active", 1210, [], ["branch"]),
        new("branch.export", "Export", "branch", "Branch", "hr", "HR Management", "export", 1220, ["branch_report_download"], []),
        // Zone
        new("zone.view", "View Listing", "zone", "Zone", "hr", "HR Management", "view", 1230, ["division"], []),
        new("zone.create", "Create", "zone", "Zone", "hr", "HR Management", "create", 1240, [], ["division"]),
        new("zone.edit", "Edit", "zone", "Zone", "hr", "HR Management", "edit", 1250, [], ["division"]),
        new("zone.delete", "Delete", "zone", "Zone", "hr", "HR Management", "delete", 1260, [], ["division"]),
        new("zone.active", "Activate / Deactivate", "zone", "Zone", "hr", "HR Management", "active", 1270, [], ["division"]),
        new("zone.export", "Export", "zone", "Zone", "hr", "HR Management", "export", 1280, ["division_report_download"], []),
        // Designation
        new("designation.view", "View Listing", "designation", "Designation", "hr", "HR Management", "view", 1290, ["designation"], []),
        new("designation.create", "Create", "designation", "Designation", "hr", "HR Management", "create", 1300, [], ["designation"]),
        new("designation.edit", "Edit", "designation", "Designation", "hr", "HR Management", "edit", 1310, [], ["designation"]),
        new("designation.delete", "Delete", "designation", "Designation", "hr", "HR Management", "delete", 1320, [], ["designation"]),
        new("designation.active", "Activate / Deactivate", "designation", "Designation", "hr", "HR Management", "active", 1330, [], ["designation"]),
        new("designation.export", "Export", "designation", "Designation", "hr", "HR Management", "export", 1340, [], ["designation"]),
        // Departments
        new("department.view", "View Listing", "department", "Departments", "hr", "HR Management", "view", 1350, ["departments"], []),
        new("department.create", "Create", "department", "Departments", "hr", "HR Management", "create", 1360, [], ["departments"]),
        new("department.edit", "Edit", "department", "Departments", "hr", "HR Management", "edit", 1370, [], ["departments"]),
        new("department.delete", "Delete", "department", "Departments", "hr", "HR Management", "delete", 1380, [], ["departments"]),
        new("department.active", "Activate / Deactivate", "department", "Departments", "hr", "HR Management", "active", 1390, [], ["departments"]),
        new("department.export", "Export", "department", "Departments", "hr", "HR Management", "export", 1400, ["department_report_download"], []),
        // ---------- User Management ----------
        // User Details
        new("user.view", "View Listing", "user", "User Details", "users", "User Management", "view", 1410, ["user_access"], []),
        new("user.create", "Create", "user", "User Details", "users", "User Management", "create", 1420, ["user_create"], []),
        new("user.edit", "Edit", "user", "User Details", "users", "User Management", "edit", 1430, ["user_edit"], []),
        new("user.delete", "Delete", "user", "User Details", "users", "User Management", "delete", 1440, ["user_delete"], []),
        new("user.active", "Activate / Deactivate", "user", "User Details", "users", "User Management", "active", 1450, ["user_active"], []),
        new("user.export", "Export", "user", "User Details", "users", "User Management", "export", 1460, ["user_download"], []),
        new("user.import", "Import", "user", "User Details", "users", "User Management", "import", 1470, ["user_upload"], []),
        new("user.template", "Download Template", "user", "User Details", "users", "User Management", "template", 1480, ["user_template"], []),
        // User App Details
        new("user_app.view", "View Listing", "user_app", "User App Details", "users", "User Management", "view", 1490, ["user_app_details_access"], []),
        new("user_app.force_logout", "Force Logout", "user_app", "User App Details", "users", "User Management", "force_logout", 1500, ["user_app_force_logout"], []),
        new("user_app.reset_device", "Remove Device UUID", "user_app", "User App Details", "users", "User Management", "reset_device", 1510, ["user_app_uuid_reset"], []),
        // User Live Activity
        new("user_activity.view", "View Live Activity", "user_activity", "User Live Activity", "users", "User Management", "view", 1520, ["user_location"], []),
        // User Target
        new("user_target.view", "View Listing", "user_target", "User Target", "users", "User Management", "view", 1530, ["target_access", "target_users_access"], []),
        new("user_target.create", "Create", "user_target", "User Target", "users", "User Management", "create", 1540, ["target_users_access_create"], []),
        new("user_target.edit", "Edit", "user_target", "User Target", "users", "User Management", "edit", 1550, ["target_users_access_edit"], []),
        new("user_target.delete", "Delete", "user_target", "User Target", "users", "User Management", "delete", 1560, ["target_users_access_delete"], []),
        new("user_target.export", "Export", "user_target", "User Target", "users", "User Management", "export", 1570, ["sales_target_users_download"], []),
        new("user_target.import", "Import", "user_target", "User Target", "users", "User Management", "import", 1580, ["sales_target_users_upload"], []),
        new("user_target.template", "Download Template", "user_target", "User Target", "users", "User Management", "template", 1590, ["sales_target_users_template"], []),
        // ---------- Account Management ----------
        // Expenses Type
        new("expense_type.view", "View Listing", "expense_type", "Expenses Type", "accounts", "Account Management", "view", 1600, ["expenses_type"], []),
        new("expense_type.create", "Create", "expense_type", "Expenses Type", "accounts", "Account Management", "create", 1610, ["expenses_type_create"], []),
        new("expense_type.edit", "Edit", "expense_type", "Expenses Type", "accounts", "Account Management", "edit", 1620, ["expenses_type_update"], []),
        new("expense_type.delete", "Delete", "expense_type", "Expenses Type", "accounts", "Account Management", "delete", 1630, [], ["expenses_type_update"]),
        new("expense_type.active", "Activate / Deactivate", "expense_type", "Expenses Type", "accounts", "Account Management", "active", 1640, [], ["expenses_type_update"]),
        // Expense
        new("expense.view", "View Listing", "expense", "Expense", "accounts", "Account Management", "view", 1650, ["expense_access"], []),
        new("expense.create", "Create", "expense", "Expense", "accounts", "Account Management", "create", 1660, ["expenses_create"], []),
        new("expense.edit", "Edit", "expense", "Expense", "accounts", "Account Management", "edit", 1670, ["expenses_edit"], []),
        new("expense.delete", "Delete", "expense", "Expense", "accounts", "Account Management", "delete", 1680, ["expenses_delete"], []),
        new("expense.check", "Check as Reporting Manager", "expense", "Expense", "accounts", "Account Management", "check", 1690, ["expense_checked"], []),
        new("expense.approve", "Approve / Reject", "expense", "Expense", "accounts", "Account Management", "approve", 1700, ["expenses_authority"], []),
        // ---------- Order Management ----------
        // Orders
        new("order.view", "View Listing", "order", "Orders", "orders", "Order Management", "view", 1710, ["order_access"], []),
        new("order.detail", "View Detail", "order", "Orders", "orders", "Order Management", "detail", 1720, ["order_show"], []),
        new("order.create", "Create", "order", "Orders", "orders", "Order Management", "create", 1730, ["order_create"], []),
        new("order.edit", "Edit", "order", "Orders", "orders", "Order Management", "edit", 1740, ["order_edit"], []),
        new("order.delete", "Delete", "order", "Orders", "orders", "Order Management", "delete", 1750, ["order_delete"], []),
        new("order.active", "Activate / Deactivate", "order", "Orders", "orders", "Order Management", "active", 1760, ["order_active"], []),
        new("order.export", "Export", "order", "Orders", "orders", "Order Management", "export", 1770, ["order_download"], []),
        new("order.dispatch", "Dispatch", "order", "Orders", "orders", "Order Management", "dispatch", 1780, ["order_dispatch"], []),
        // Order Dispatch
        new("order_dispatch.view", "View Listing", "order_dispatch", "Order Dispatch", "orders", "Order Management", "view", 1790, ["sale_access"], []),
        new("order_dispatch.detail", "View Detail", "order_dispatch", "Order Dispatch", "orders", "Order Management", "detail", 1800, ["sale_show"], []),
        // ---------- Loyalty Management ----------
        // Invoices Transaction
        new("invoice_transaction.view", "View Listing", "invoice_transaction", "Invoices Transaction", "loyalty", "Loyalty Management", "view", 1810, ["new_invoice_access"], []),
        new("invoice_transaction.detail", "View Detail", "invoice_transaction", "Invoices Transaction", "loyalty", "Loyalty Management", "detail", 1815, [], ["new_invoice_access", "invoice_transaction.view"]),
        new("invoice_transaction.create", "Create", "invoice_transaction", "Invoices Transaction", "loyalty", "Loyalty Management", "create", 1820, ["new_invoice_create"], []),
        new("invoice_transaction.edit", "Edit", "invoice_transaction", "Invoices Transaction", "loyalty", "Loyalty Management", "edit", 1830, ["new_invoice_edit"], []),
        new("invoice_transaction.delete", "Delete", "invoice_transaction", "Invoices Transaction", "loyalty", "Loyalty Management", "delete", 1840, ["new_invoice_delete"], []),
        new("invoice_transaction.export", "Export", "invoice_transaction", "Invoices Transaction", "loyalty", "Loyalty Management", "export", 1850, ["new_invoice_export"], []),
        new("invoice_transaction.approve_ss", "Approve by SS", "invoice_transaction", "Invoices Transaction", "loyalty", "Loyalty Management", "approve_ss", 1860, ["new_invoice_approve_ss"], []),
        new("invoice_transaction.approve_sales", "Approve by Sales", "invoice_transaction", "Invoices Transaction", "loyalty", "Loyalty Management", "approve_sales", 1870, ["new_invoice_approve_sales"], []),
        new("invoice_transaction.approve_ho", "Approve by HO", "invoice_transaction", "Invoices Transaction", "loyalty", "Loyalty Management", "approve_ho", 1880, ["new_invoice_approve_ho"], []),
        new("invoice_transaction.hold", "Hold", "invoice_transaction", "Invoices Transaction", "loyalty", "Loyalty Management", "hold", 1890, ["new_invoice_hold"], []),
        new("invoice_transaction.reject", "Reject", "invoice_transaction", "Invoices Transaction", "loyalty", "Loyalty Management", "reject", 1900, ["new_invoice_reject"], []),
        // Scheme Creation
        new("scheme.view", "View Listing", "scheme", "Scheme Creation", "loyalty", "Loyalty Management", "view", 1910, ["scheme_access_list", "scheme_access"], []),
        new("scheme.detail", "View Detail", "scheme", "Scheme Creation", "loyalty", "Loyalty Management", "detail", 1920, ["scheme_show"], []),
        new("scheme.create", "Create", "scheme", "Scheme Creation", "loyalty", "Loyalty Management", "create", 1930, ["scheme_create"], []),
        new("scheme.edit", "Edit", "scheme", "Scheme Creation", "loyalty", "Loyalty Management", "edit", 1940, ["scheme_edit"], []),
        new("scheme.delete", "Delete", "scheme", "Scheme Creation", "loyalty", "Loyalty Management", "delete", 1950, ["scheme_delete"], []),
        new("scheme.draft", "Send to Draft", "scheme", "Scheme Creation", "loyalty", "Loyalty Management", "draft", 1960, ["scheme_draft"], []),
        new("scheme.submit", "Submit to Next Level", "scheme", "Scheme Creation", "loyalty", "Loyalty Management", "submit", 1970, ["scheme_submit"], []),
        new("scheme.approve", "Approve", "scheme", "Scheme Creation", "loyalty", "Loyalty Management", "approve", 1980, ["scheme_approve"], []),
        new("scheme.reject", "Reject", "scheme", "Scheme Creation", "loyalty", "Loyalty Management", "reject", 1990, ["scheme_reject"], []),
        new("scheme.publish", "Publish", "scheme", "Scheme Creation", "loyalty", "Loyalty Management", "publish", 2000, ["scheme_publish"], []),
        // Redemption
        new("redemption.view", "View Listing", "redemption", "Redemption", "loyalty", "Loyalty Management", "view", 2010, ["redemption_access"], []),
        new("redemption.export", "Export", "redemption", "Redemption", "loyalty", "Loyalty Management", "export", 2020, ["redemption_download"], []),
        // ---------- Beats Management ----------
        // Beats
        new("beat.view", "View Listing", "beat", "Beats", "beats", "Beats Management", "view", 2030, ["beat_access"], []),
        new("beat.detail", "View Detail", "beat", "Beats", "beats", "Beats Management", "detail", 2040, ["beat_show"], []),
        new("beat.create", "Create", "beat", "Beats", "beats", "Beats Management", "create", 2050, ["beat_create"], []),
        new("beat.edit", "Edit", "beat", "Beats", "beats", "Beats Management", "edit", 2060, ["beat_edit"], []),
        new("beat.delete", "Delete", "beat", "Beats", "beats", "Beats Management", "delete", 2070, ["beat_delete"], []),
        // Beat Detail
        new("beat_detail.view", "View Listing", "beat_detail", "Beat Detail", "beats", "Beats Management", "view", 2080, ["beatdetail_access"], []),
        // Checkin-Checkout
        new("checkin.view", "View Listing", "checkin", "Checkin-Checkout", "beats", "Beats Management", "view", 2090, ["checkin_access"], []),
        new("checkin.export", "Export", "checkin", "Checkin-Checkout", "beats", "Beats Management", "export", 2100, ["checkin_download"], []),
        // Check In & Check Out Report
        new("visit_report.view", "View Report", "visit_report", "Check In & Check Out Report", "beats", "Beats Management", "view", 2110, ["visit_report"], []),
        // ---------- Reports Management ----------
        // ASR Performance
        new("asr_performance_report.export", "Export", "asr_performance_report", "ASR Performance", "reports", "Reports Management", "export", 2120, ["ASR_report_Download"], []),
        // Rating Report
        new("rating_report.view", "View Report", "rating_report", "Rating Report", "reports", "Reports Management", "view", 2130, ["asm_rating_report"], []),
        new("rating_report.export", "Export", "rating_report", "Rating Report", "reports", "Reports Management", "export", 2140, ["asm_rating_download", "asm_rating_detailed_download"], []),
        // Activity Reports
        new("activity_report.view", "View Report", "activity_report", "Activity Reports", "reports", "Reports Management", "view", 2160, ["activity_report_access"], []),
        new("activity_report.export_sales_engineer", "Sales Engineer Wise Export", "activity_report", "Activity Reports", "reports", "Reports Management", "export_sales_engineer", 2170, ["activity_report_sales_engineer_download"], []),
        new("activity_report.export_distributor", "Distributor Wise Export", "activity_report", "Activity Reports", "reports", "Reports Management", "export_distributor", 2180, ["activity_report_distributor_download"], []),
        new("activity_report.export_gift_summary", "Gift Summary Export", "activity_report", "Activity Reports", "reports", "Reports Management", "export_gift_summary", 2190, ["activity_report_gift_summary_download"], []),
        // Retailer Performance
        new("retailer_performance_report.export", "Export", "retailer_performance_report", "Retailer Performance", "reports", "Reports Management", "export", 2200, ["retailer_productivity_report"], []),
        // Dealer Performance
        new("dealer_performance_report.export", "Export", "dealer_performance_report", "Dealer Performance", "reports", "Reports Management", "export", 2210, [], ["retailer_productivity_report"]),
        // Market Intelligence
        new("market_intelligence_report.view", "View Report", "market_intelligence_report", "Market Intelligence", "reports", "Reports Management", "view", 2220, ["market_intelligence_access"], []),
        new("market_intelligence_report.export", "Export", "market_intelligence_report", "Market Intelligence", "reports", "Reports Management", "export", 2230, ["market_intelligence_report_download"], []),
        // ---------- Setting Management ----------
        // FieldKonnect App Setting
        new("app_setting.view", "View Setting", "app_setting", "FieldKonnect App Setting", "settings", "Setting Management", "view", 2240, ["loyalty_app_setting_access", "field_konnect_app_setting_access"], []),
        new("app_setting.edit", "Save Setting", "app_setting", "FieldKonnect App Setting", "settings", "Setting Management", "edit", 2250, [], ["loyalty_app_setting_access", "field_konnect_app_setting_access"]),
        // Dealer Portal Setting
        new("dealer_portal_setting.view", "View Setting", "dealer_portal_setting", "Dealer Portal Setting", "settings", "Setting Management", "view", 2260, ["dealer_portal_setting_access"], []),
        // Roles
        new("role.view", "View Listing", "role", "Roles", "settings", "Setting Management", "view", 2270, ["role_access"], []),
        new("role.create", "Create", "role", "Roles", "settings", "Setting Management", "create", 2280, ["role_create"], []),
        new("role.edit", "Edit", "role", "Roles", "settings", "Setting Management", "edit", 2290, ["role_edit"], []),
        new("role.delete", "Delete", "role", "Roles", "settings", "Setting Management", "delete", 2300, ["role_delete"], []),
    ];

    /// <summary>Old name to new name, for callers that still send a legacy permission.</summary>
    public static IReadOnlyDictionary<string, string> LegacyToCurrent { get; } =
        All.SelectMany(definition => definition.LegacyNames.Select(legacy => (legacy, definition.Name)))
            .ToDictionary(pair => pair.legacy, pair => pair.Name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Permission names the SFA app builds released before V7.3 still look for.
/// The login and profile payloads carry these alongside the current names so an installed
/// app keeps working after the rename. Drop this once every field device is on a build
/// that reads the current names.</summary>
public static class LegacyMobilePermissionAliases
{
    public static IReadOnlyDictionary<string, string[]> ByCurrentName { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["customer.approve"] = ["retailer_approve"],
        ["expense.check"] = ["expense_checked"],
        ["expense.approve"] = ["expenses_authority"],
        ["expense.create"] = ["expenses_create"],
        ["expense.edit"] = ["expenses_edit"],
        ["expense.delete"] = ["expenses_delete"],
    };

    public static IEnumerable<string> Expand(IEnumerable<string> currentNames)
    {
        var names = new List<string>();
        foreach (var name in currentNames)
        {
            names.Add(name);
            if (ByCurrentName.TryGetValue(name, out var aliases)) names.AddRange(aliases);
        }

        return names;
    }
}
