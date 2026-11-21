# The "web" resource: AddDockerfile in the AppHost points here, and Deployify builds it with `docker build`.
FROM nginx:1.29-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY index.html /usr/share/nginx/html/index.html
EXPOSE 8085
