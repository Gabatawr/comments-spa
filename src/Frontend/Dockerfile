# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Stage 1 — build the Angular bundle (node 22 + Angular CLI)
# ---------------------------------------------------------------------------
FROM node:22-alpine AS build

WORKDIR /app

# Манифесты отдельно — слой с зависимостями кэшируется и переиспользуется.
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

# Конфиги и исходники приложения.
COPY angular.json tsconfig.json tsconfig.app.json ./
COPY public ./public
COPY src ./src

RUN npx ng build --configuration production

# ---------------------------------------------------------------------------
# Stage 2 — nginx serves dist/comments-spa/browser and proxies API/WS
# ---------------------------------------------------------------------------
FROM nginx:alpine AS runtime

COPY nginx.conf /etc/nginx/conf.d/default.conf
RUN chmod 644 /etc/nginx/conf.d/default.conf

COPY --from=build /app/dist/comments-spa/browser /usr/share/nginx/html

# Файлы в общем рабочем каталоге создаются с mode 600; nginx worker (uid 101)
# иначе получит 403. Нормализуем права на статику.
RUN chmod -R a+rX /usr/share/nginx/html && chown -R nginx:nginx /usr/share/nginx/html

EXPOSE 80

HEALTHCHECK --interval=15s --timeout=3s --start-period=5s --retries=5 \
  CMD wget -q -O /dev/null http://127.0.0.1/ || exit 1

CMD ["nginx", "-g", "daemon off;"]
