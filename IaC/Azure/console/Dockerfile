# ── Build stage ──────────────────────────────────────────
# Build context must be IaC/Azure/ (parent dir).
# Build with: docker build -f console/Dockerfile -t openclaw-console .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY console/*.csproj ./
RUN dotnet restore
COPY console/ ./
RUN dotnet publish -c Release -o /app/publish

# ── Runtime stage ────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Install Python, Ansible, Azure CLI, SSH client, nginx, supervisor
RUN apt-get update && apt-get install -y --no-install-recommends \
        python3 python3-pip python3-venv \
        openssh-client sshpass curl gnupg lsb-release \
        nginx supervisor \
    && python3 -m venv /opt/ansible-venv \
    && /opt/ansible-venv/bin/pip install --no-cache-dir ansible-core azure-cli \
    && /opt/ansible-venv/bin/ansible-galaxy collection install ansible.windows \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

ENV PATH="/opt/ansible-venv/bin:${PATH}"

# Copy Ansible playbooks (from build context IaC/Azure/)
COPY ansible/ /app/ansible/

# Copy published .NET app
COPY --from=build /app/publish ./

# nginx + supervisor config
COPY console/nginx/nginx.conf /etc/nginx/nginx.conf
COPY console/supervisord.conf /etc/supervisor/conf.d/app.conf

# dotnet listens on 5000 internally; nginx fronts on 8080
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 8080

# supervisord launches both nginx and dotnet
CMD ["/usr/bin/supervisord", "-c", "/etc/supervisor/conf.d/app.conf"]
