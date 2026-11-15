job "ingress-gateway" {
  namespace = "mesh"
  datacenters = ["dc1"]
  type = "service"

  meta = {
    "deployify-managed" = "true"
  }

  group "ingress-gateway" {
    count = 1

    network {
      mode = "bridge"
      port "inbound_api" {
        static = 8080
        to = 8080
      }
      port "inbound_web" {
        static = 3000
        to = 3000
      }
    }

    service {
      name = "ingress-gateway"
      port = "8080"

      connect {
        gateway {
          proxy {}

          ingress {
            listener {
              port = 8080
              protocol = "tcp"
              service {
                name = "api"
              }
            }
            listener {
              port = 3000
              protocol = "tcp"
              service {
                name = "web"
              }
            }
          }
        }
      }
    }
  }
}
