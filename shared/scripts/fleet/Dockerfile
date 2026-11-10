# A mock fleet of Bambu Lab printers for Bambu Bar (see fleet.py).
FROM python:3.12-alpine
RUN apk add --no-cache mosquitto openssl && pip install --no-cache-dir paho-mqtt==2.1.0
WORKDIR /app
COPY fleet/fleet.py emulator/p2s-sample.json emulator/a1-sample.json emulator/h3c-sample.json ./
CMD ["python", "-u", "fleet.py"]
