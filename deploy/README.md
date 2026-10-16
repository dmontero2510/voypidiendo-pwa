# Despliegue de VoyPidiendo en un Cloud Server

Este paquete instala una única plataforma multiempresa con:

- PWA/API;
- Superadministrador;
- SQL Server Express para `VOYPIDIENDO_CENTRAL`;
- Nginx como proxy;
- certificados Let's Encrypt automáticos;
- claves de cifrado persistentes y compartidas;
- logs y backups persistentes.

Las bases comerciales permanecen en los servidores de cada cliente.

## Servidor recomendado

- Ubuntu Server 24.04 LTS;
- 4 vCPU;
- 8 GB RAM;
- 80/100 GB SSD;
- IPv4 pública fija;
- snapshot y backup externo.

## DNS inicial

Crear en Cloudflare, inicialmente como `DNS only`:

```text
A  estrella  IP_DEL_CLOUD_SERVER
A  control   IP_DEL_CLOUD_SERVER
```

## Instalación base

Ingresar por SSH como un usuario con `sudo`:

```bash
sudo apt update
sudo apt -y upgrade
sudo apt -y install ca-certificates curl git ufw
curl -fsSL https://get.docker.com -o get-docker.sh
sudo sh get-docker.sh
sudo usermod -aG docker "$USER"
```

Cerrar y volver a abrir la sesión SSH. Comprobar:

```bash
docker version
docker compose version
```

## Firewall

Antes de activarlo, permitir SSH usando el puerto informado por el proveedor:

```bash
sudo ufw allow PUERTO_SSH/tcp
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw enable
```

No publicar el puerto 1433 del SQL central.

## Publicación

```bash
sudo mkdir -p /opt/voypidiendo
sudo chown "$USER":"$USER" /opt/voypidiendo
git clone URL_REPOSITORIO_PRIVADO /opt/voypidiendo
cd /opt/voypidiendo/deploy
cp .env.example .env
nano .env
mkdir -p backups logs/pwa logs/superadmin
chmod 700 .env backups
docker compose build
docker compose up -d
docker compose ps
```

Usar claves diferentes, aleatorias y largas en `.env`. No enviar ni guardar ese archivo en GitHub.

## Primer ingreso

1. Abrir `https://control.voypidiendo.net.ar`.
2. Crear el Superadministrador usando `SUPERADMIN_BOOTSTRAP_KEY`.
3. Crear Distribuidora La Estrella.
4. Guardar su conexión SQL en el campo seguro.
5. Configurar el dominio `estrella.voypidiendo.net.ar`.
6. Probar conexión y validar estructura.
7. Abrir `https://estrella.voypidiendo.net.ar` y probar el login.

## Actualización

```bash
cd /opt/voypidiendo
git pull
cd deploy
docker compose build
docker compose up -d
docker image prune -f
```

La carpeta/volumen de claves y la base central no se eliminan al actualizar.

## Backup diario

```bash
cd /opt/voypidiendo/deploy
chmod +x backup-central.sh restore-central.sh
./backup-central.sh
crontab -e
```

Ejemplo para ejecutar todos los días a las 02:30:

```cron
30 2 * * * /opt/voypidiendo/deploy/backup-central.sh >> /opt/voypidiendo/deploy/logs/backup.log 2>&1
```

Copiar además los `.bak` a otro proveedor o almacenamiento. Un backup guardado únicamente en el mismo VPS no protege ante una pérdida completa del servidor.

## Archivos que nunca deben perderse

- volumen Docker `voypidiendo_central_sql`;
- volumen Docker `voypidiendo_shared_keys`;
- `deploy/.env`;
- backups de `VOYPIDIENDO_CENTRAL`;
- configuración DNS y acceso al repositorio privado.

Sin el volumen `shared_keys`, las conexiones SQL cifradas no podrán recuperarse.

## Conexión con cada cliente

Opción recomendada: WireGuard entre el Cloud Server y la red del cliente.

Opción transitoria: publicar un puerto SQL con TLS y restringirlo en el firewall exclusivamente a la IP pública del Cloud Server. Nunca permitir SQL desde cualquier IP.
