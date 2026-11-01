# A mock Bambu Lab printer camera for Bambu Bar's integration tests (see camera.py).
FROM python:3.12-alpine
RUN apk add --no-cache openssl
WORKDIR /app
COPY camera/camera.py ./
EXPOSE 322 6000 8080
CMD ["python", "-u", "camera.py"]
