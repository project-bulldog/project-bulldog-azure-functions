# 🧠 Codename Bulldog – Azure Function Backend

This is the Azure Function backend for Codename Bulldog, a productivity AI app that ingests user input (e.g., meeting notes, transcripts, or email content) and intelligently extracts:

- ✅ Actionable tasks
- 📜 Summaries
- ⏰ Reminders
- 📦 Stores results in the database or pushes them to the frontend

The backend is built using .NET 8 Isolated Azure Functions, integrated with OpenAI, and exposed via RESTful endpoints.

## 🔁 CI/CD Environments

| Environment | Branch | Azure App Name         | Deployment Status                                                                                                                              |
| ----------- | ------ | ---------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------- |
| QA          | qa     | bulldog-qa-functions   | ![QA Status](https://github.com/project-bulldog/project-bulldog-azure-functions/actions/workflows/deploy-functions-qa.yml/badge.svg)           |
| Production  | main   | bulldog-prod-functions | ![Production Status](https://github.com/project-bulldog/project-bulldog-azure-functions/actions/workflows/deploy-functions-prod.yml/badge.svg) |

Pushes to `qa` auto-deploy to a staging Function App.  
Pushes to `main` auto-deploy to your production Azure Function.

## 🛠 Tech Stack

- ✅ Azure Functions (Isolated Worker Model, .NET 8)
- 🧠 OpenAI GPT (via encapsulated AI service)
- 📓 Azure Blob Storage
- 📊 Azure Application Insights
- 🔐 GitHub Actions with Service Principal for CI/CD

## 📂 Project Structure
