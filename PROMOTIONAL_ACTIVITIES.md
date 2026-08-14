# Promotional Activities

The mobile Activity tab uses a server-driven form. `GET /api/activities/config/{type}` defines the labels, expense rows, dealer-share behavior and photo limit for Nukkad, Retailer, Farmer and Influencer activities.

Authenticated endpoints: `GET/POST /api/activities`, `GET/PUT /api/activities/{id}`, `POST /api/activities/{id}/submit`, `POST /api/activities/upload`, `GET /api/distributors/search`, `GET /api/reports/all-meeting-summary`, `GET /api/reports/kyc-summary`, and `GET /api/reports/kyc-summary/asr-wise`.

Drafts can be edited; submitted activities are immutable. The app also keeps an offline MMKV draft per activity type. Apply migration `20260813092624_AddPromotionalActivities` or the additive SQL script under `database/releases/V6.3` before enabling the module.
