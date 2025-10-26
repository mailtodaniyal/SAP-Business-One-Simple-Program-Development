# SAP Business One Supplier Integration

This application integrates **SAP Business One** with the external service **[quepagar.com](https://quepagar.com)**.  

It provides three main features:  
1. Manage a list of *affiliated suppliers* (manually or by importing Excel).  
2. Run a scheduled job (cron) to send unpaid or updated supplier invoices/credit notes to the external API.  
3. Expose a simple web service for token validation and on-demand invoice queries.  

---

## 📂 Project Structure
```
SapIntegrationApp/
│── Program.cs              # Main application code
│── SapIntegrationApp.csproj
│── appsettings.json        # Configuration file (optional, can also use env vars)
│── appdata.db              # Auto-generated SQLite database
│── README.md               # This file
```

---

## ⚙️ Requirements
- Windows Server or workstation  
- .NET 7.0 (or higher) SDK  
- SAP Business One client + **DI API** installed  
- Access to an SAP company database  

---

## 🔧 Configuration
You can configure the app either via **environment variables** or `appsettings.json`.  

### Option 1: Environment Variables
Set the following:
```
SapDbServer=SAP_SERVER
SapDbName=SBODEMOUS
SapUser=manager
SapPass=manager
SapDbUser=sa
SapDbPass=sqlpass
SapCompanyDb=SBODEMOUS
QuePagarBase=https://quepagar.com/api/v1
QuePagarApiKey=ABC
CronMinutes=15
SqliteFile=appdata.db
FreelancerId=YOUR-FREELANCER-ID
```

### Option 2: `appsettings.json`
Edit the provided `appsettings.json` file with the same values.  

---

## ▶️ Running the Application
1. Clone or extract the project folder.  
2. Install dependencies:
   ```bash
   dotnet restore
   ```
3. Build the project:
   ```bash
   dotnet build
   ```
4. Run the program:
   ```bash
   dotnet run
   ```

On first run, the app will:
- Connect to SAP Business One  
- Create the local SQLite database (`appdata.db`)  
- Start the cron scheduler  
- Expose local web APIs  

---

## 🌐 Exposed Web APIs
### `GET /token`
Validate user credentials and return a token.  

### `POST /queryDocuments`
Send a list of document IDs in JSON body and retrieve full document info.  

---

## 📡 Scheduled Job
Every `t` minutes (default: 15), the app will:
1. Collect new or updated unpaid invoices and credit notes from affiliated suppliers.  
2. Send them to `https://quepagar.com/api/v1/document` with a valid token.  
3. Retry failed transmissions until successful.  

---

## 🗄️ Data Storage
- **Suppliers**: stored in SQLite (`appdata.db`)  
- **Sent logs**: timestamps of successful transmissions  
- **User tokens**: temporary login sessions  

---

## 📝 Notes
- Requires SAP DI API → ensure **SAP Business One client** and **DI API** are installed.  
- SQLite database is automatically created in the project directory.  
- To import suppliers from Excel, place your `.xlsx` file in the working folder (the app will detect it).  
